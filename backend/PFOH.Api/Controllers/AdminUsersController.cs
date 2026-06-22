using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = "AdminOnly")]
public class AdminUsersController(IConfiguration configuration, ILogger<AdminUsersController> logger) : ControllerBase
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<AdminUserRoleDto>>> SearchUsers(
        [FromQuery] string? q,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            {
                return Ok(Array.Empty<AdminUserRoleDto>());
            }

            var context = await GetAdminRoleContextAsync(ct);
            var adminAssignments = await GetAdminAssignmentsAsync(context, ct);
            var adminPrincipalIds = adminAssignments
                .Where(a => a.AppRoleId == context.AdminAppRoleId)
                .Select(a => a.PrincipalId)
                .ToHashSet();

            var term = EscapeODataString(q.Trim());
            var filter = $"startswith(displayName,'{term}') or startswith(mail,'{term}') or startswith(userPrincipalName,'{term}')";
            var path = $"users?$select=id,displayName,mail,userPrincipalName&$top=15&$filter={Uri.EscapeDataString(filter)}";

            var users = await GraphSendAsync<GraphCollection<GraphUser>>(HttpMethod.Get, path, null, ct);

            var results = users.Value
                .Select(user => new AdminUserRoleDto(
                    user.Id,
                    user.DisplayName,
                    user.Mail,
                    user.UserPrincipalName,
                    Guid.TryParse(user.Id, out var userId) && adminPrincipalIds.Contains(userId)))
                .OrderBy(u => u.DisplayName ?? u.Mail ?? u.UserPrincipalName)
                .ToList();

            return Ok(results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin user search failed for query {Query}.", q);

            return Problem(
                title: "Admin user search failed.",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [HttpPost("{userObjectId:guid}/admin-role")]
    public async Task<ActionResult<AdminUserRoleDto>> GrantAdminRole(
        Guid userObjectId,
        CancellationToken ct)
    {
        try
        {
            var context = await GetAdminRoleContextAsync(ct);
            var user = await GetUserAsync(userObjectId, ct);
            var existing = await FindAdminAssignmentAsync(context, userObjectId, ct);

            if (existing is null)
            {
                await GraphSendAsync<JsonElement>(
                    HttpMethod.Post,
                    $"servicePrincipals/{context.ResourceServicePrincipalObjectId}/appRoleAssignedTo",
                    new
                    {
                        principalId = userObjectId,
                        resourceId = context.ResourceServicePrincipalId,
                        appRoleId = context.AdminAppRoleId
                    },
                    ct);
            }

            return Ok(ToDto(user, true));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grant admin role failed for user {UserObjectId}.", userObjectId);

            return Problem(
                title: "Grant admin role failed.",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [HttpDelete("{userObjectId:guid}/admin-role")]
    public async Task<ActionResult<AdminUserRoleDto>> RemoveAdminRole(
        Guid userObjectId,
        CancellationToken ct)
    {
        try
        {
            var signedInUserObjectId = User.GetExternalObjectId();

            if (Guid.TryParse(signedInUserObjectId, out var signedInUserGuid) && signedInUserGuid == userObjectId)
            {
                return BadRequest("You cannot remove your own administrator role from this screen.");
            }

            var context = await GetAdminRoleContextAsync(ct);
            var user = await GetUserAsync(userObjectId, ct);
            var existing = await FindAdminAssignmentAsync(context, userObjectId, ct);

            if (existing is not null)
            {
                await GraphSendNoContentAsync(
                    HttpMethod.Delete,
                    $"servicePrincipals/{context.ResourceServicePrincipalObjectId}/appRoleAssignedTo/{existing.Id}",
                    ct);
            }

            return Ok(ToDto(user, false));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Remove admin role failed for user {UserObjectId}.", userObjectId);

            return Problem(
                title: "Remove admin role failed.",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private AdminGraphSettings GetSettings()
    {
        var tenantId = configuration["GraphAdmin:TenantId"] ?? configuration["AzureAd:TenantId"];
        var clientId = configuration["GraphAdmin:ClientId"] ?? configuration["AzureAd:ClientId"];
        var clientSecret = configuration["GraphAdmin:ClientSecret"] ?? configuration["AzureAd:ClientSecret"];
        var resourceAppClientId = configuration["GraphAdmin:ResourceAppClientId"] ?? configuration["AzureAd:ClientId"];
        var adminAppRoleValue = configuration["GraphAdmin:AdminAppRoleValue"] ?? "PFOH.Admin";

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(resourceAppClientId))
        {
            throw new InvalidOperationException(
                "Missing Graph admin configuration. Set GraphAdmin__TenantId, GraphAdmin__ClientId, GraphAdmin__ClientSecret, and GraphAdmin__ResourceAppClientId, or provide equivalent AzureAd settings.");
        }

        return new AdminGraphSettings(
            tenantId,
            clientId,
            clientSecret,
            resourceAppClientId,
            adminAppRoleValue);
    }

    private async Task<AdminRoleContext> GetAdminRoleContextAsync(CancellationToken ct)
    {
        var settings = GetSettings();
        var filter = $"appId eq '{EscapeODataString(settings.ResourceAppClientId)}'";
        var path = $"servicePrincipals?$filter={Uri.EscapeDataString(filter)}&$select=id,appId,displayName,appRoles";
        var servicePrincipals = await GraphSendAsync<GraphCollection<GraphServicePrincipal>>(HttpMethod.Get, path, null, ct);

        var servicePrincipal = servicePrincipals.Value.FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not find the API service principal for appId {settings.ResourceAppClientId}.");

        if (!Guid.TryParse(servicePrincipal.Id, out var resourceServicePrincipalId))
        {
            throw new InvalidOperationException("The API service principal id returned by Microsoft Graph is not a GUID.");
        }

        var adminRole = servicePrincipal.AppRoles?
            .FirstOrDefault(role =>
                role.IsEnabled &&
                string.Equals(role.Value, settings.AdminAppRoleValue, StringComparison.OrdinalIgnoreCase));

        if (adminRole is null)
        {
            throw new InvalidOperationException($"Could not find an enabled app role with value '{settings.AdminAppRoleValue}' on the API service principal.");
        }

        return new AdminRoleContext(servicePrincipal.Id, resourceServicePrincipalId, adminRole.Id);
    }

    private async Task<IReadOnlyList<GraphAppRoleAssignment>> GetAdminAssignmentsAsync(
        AdminRoleContext context,
        CancellationToken ct)
    {
        var assignments = await GraphSendAsync<GraphCollection<GraphAppRoleAssignment>>(
            HttpMethod.Get,
            $"servicePrincipals/{context.ResourceServicePrincipalObjectId}/appRoleAssignedTo?$select=id,principalId,principalDisplayName,principalType,resourceId,appRoleId",
            null,
            ct);

        return assignments.Value
            .Where(a => a.AppRoleId == context.AdminAppRoleId)
            .ToList();
    }

    private async Task<GraphAppRoleAssignment?> FindAdminAssignmentAsync(
        AdminRoleContext context,
        Guid userObjectId,
        CancellationToken ct)
    {
        var assignments = await GetAdminAssignmentsAsync(context, ct);
        return assignments.FirstOrDefault(a => a.PrincipalId == userObjectId);
    }

    private async Task<GraphUser> GetUserAsync(Guid userObjectId, CancellationToken ct)
    {
        return await GraphSendAsync<GraphUser>(
            HttpMethod.Get,
            $"users/{userObjectId}?$select=id,displayName,mail,userPrincipalName",
            null,
            ct);
    }

    private static AdminUserRoleDto ToDto(GraphUser user, bool isAdmin) => new(
        user.Id,
        user.DisplayName,
        user.Mail,
        user.UserPrincipalName,
        isAdmin);

    private async Task<string> GetGraphAccessTokenAsync(CancellationToken ct)
    {
        var settings = GetSettings();
        var tokenUrl = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token";

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["grant_type"] = "client_credentials",
            ["scope"] = "https://graph.microsoft.com/.default"
        });

        using var response = await Http.PostAsync(tokenUrl, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Unable to get Microsoft Graph token: {(int)response.StatusCode} {json}");
        }

        var token = JsonSerializer.Deserialize<GraphTokenResponse>(json, JsonOptions);

        if (string.IsNullOrWhiteSpace(token?.AccessToken))
        {
            throw new InvalidOperationException("Microsoft Graph token response did not include an access token.");
        }

        return token.AccessToken;
    }

    private async Task<T> GraphSendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = await CreateGraphRequestAsync(method, path, body, ct);
        using var response = await Http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft Graph returned {(int)response.StatusCode}: {json}");
        }

        if (typeof(T) == typeof(JsonElement) && string.IsNullOrWhiteSpace(json))
        {
            return default!;
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Microsoft Graph returned an empty response.");
    }

    private async Task GraphSendNoContentAsync(
        HttpMethod method,
        string path,
        CancellationToken ct)
    {
        using var request = await CreateGraphRequestAsync(method, path, null, ct);
        using var response = await Http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft Graph returned {(int)response.StatusCode}: {json}");
        }
    }

    private async Task<HttpRequestMessage> CreateGraphRequestAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        var token = await GetGraphAccessTokenAsync(ct);
        var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{path.TrimStart('/')}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string EscapeODataString(string value) => value.Replace("'", "''");

    private sealed record AdminGraphSettings(
        string TenantId,
        string ClientId,
        string ClientSecret,
        string ResourceAppClientId,
        string AdminAppRoleValue);

    private sealed record AdminRoleContext(
        string ResourceServicePrincipalObjectId,
        Guid ResourceServicePrincipalId,
        Guid AdminAppRoleId);

    private sealed record GraphCollection<T>(List<T> Value);

    private sealed record GraphUser(
        string Id,
        string? DisplayName,
        string? Mail,
        string? UserPrincipalName);

    private sealed record GraphServicePrincipal(
        string Id,
        string AppId,
        string? DisplayName,
        List<GraphAppRole>? AppRoles);

    private sealed record GraphAppRole(
        Guid Id,
        string? Value,
        bool IsEnabled);

    private sealed record GraphAppRoleAssignment(
        string Id,
        Guid PrincipalId,
        string? PrincipalDisplayName,
        string? PrincipalType,
        Guid ResourceId,
        Guid AppRoleId);

    private sealed class GraphTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }
}
