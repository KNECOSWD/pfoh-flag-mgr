using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace PFOH.Snapshot;

/// <summary>
/// Reads directory users with the allow-listed $select. signInActivity is a second request.
/// Tokens are not logged.
/// </summary>
public sealed class GraphDirectorySource(TokenCredential credential, string graphTenantId, string graphClientId, HttpClient http) : IDirectorySource
{
    public async Task<IReadOnlyList<DirectoryUserRecord>> ReadAsync(CancellationToken ct)
    {
        var graph = new ClientAssertionCredential(
            graphTenantId,
            graphClientId,
            async cancellation =>
            {
                var exchanged = await credential.GetTokenAsync(
                    new TokenRequestContext(["api://AzureADTokenExchange"]),
                    cancellation);
                return exchanged.Token;
            });
        var token = await graph.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);
        var users = await GetPagesAsync(
            "https://graph.microsoft.com/v1.0/users?$select=" + Uri.EscapeDataString(SnapshotFieldSpec.DirectoryUserSelect),
            token.Token,
            ct);
        var activity = await GetPagesAsync(
            "https://graph.microsoft.com/v1.0/users?$select=" + Uri.EscapeDataString(SnapshotFieldSpec.SignInActivitySelect),
            token.Token,
            ct);
        var activityById = new Dictionary<string, DirectorySignInRecord?>(StringComparer.Ordinal);
        foreach (var element in activity)
        {
            var id = ReadString(element, "id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            activityById[id] = ReadActivity(element);
        }

        var records = new List<DirectoryUserRecord>(users.Count);
        foreach (var element in users)
        {
            var id = ReadString(element, "id");
            activityById.TryGetValue(id ?? string.Empty, out var signIn);
            records.Add(new DirectoryUserRecord
            {
                Id = id,
                DisplayName = ReadString(element, "displayName"),
                Mail = ReadString(element, "mail"),
                OtherMails = ReadStrings(element, "otherMails"),
                Identities = ReadIdentities(element),
                UserPrincipalName = ReadString(element, "userPrincipalName"),
                CreatedDateTime = ReadString(element, "createdDateTime"),
                AccountEnabled = element.TryGetProperty("accountEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? enabled.GetBoolean()
                    : null,
                Phone = ReadString(element, SnapshotFieldSpec.PhoneExtension),
                Organization = ReadString(element, SnapshotFieldSpec.OrganizationExtension),
                SignInActivity = signIn
            });
        }

        return records;
    }

    private async Task<List<JsonElement>> GetPagesAsync(string firstUrl, string accessToken, CancellationToken ct)
    {
        var pages = new List<JsonElement>();
        var url = firstUrl;
        while (!string.IsNullOrEmpty(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || !parsed.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme != "https")
            {
                throw new InvalidOperationException("Microsoft Graph returned a page link outside graph.microsoft.com.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, parsed);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Microsoft Graph returned " + (int)response.StatusCode + ".");
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    pages.Add(item.Clone());
                }
            }

            url = document.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }

        return pages;
    }

    private static DirectorySignInRecord? ReadActivity(JsonElement user)
    {
        if (!user.TryGetProperty("signInActivity", out var activity) || activity.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new DirectorySignInRecord
        {
            LastSignInDateTime = ReadString(activity, "lastSignInDateTime"),
            LastSuccessfulSignInDateTime = ReadString(activity, "lastSuccessfulSignInDateTime")
        };
    }

    private static List<DirectoryIdentityRecord> ReadIdentities(JsonElement user)
    {
        var identities = new List<DirectoryIdentityRecord>();
        if (!user.TryGetProperty("identities", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return identities;
        }

        foreach (var item in array.EnumerateArray())
        {
            identities.Add(new DirectoryIdentityRecord
            {
                SignInType = ReadString(item, "signInType"),
                IssuerAssignedId = ReadString(item, "issuerAssignedId")
            });
        }

        return identities;
    }

    private static List<string> ReadStrings(JsonElement element, string name)
    {
        var values = new List<string>();
        if (!element.TryGetProperty(name, out var property))
        {
            return values;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                values.Add(text);
            }

            return values;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    values.Add(text);
                }
            }
        }

        return values;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }
}
