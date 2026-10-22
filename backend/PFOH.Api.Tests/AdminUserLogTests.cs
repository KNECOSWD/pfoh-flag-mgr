using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PFOH.Api.Controllers;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class AdminUserLogTests
{
    private const string SearchTerm = "pii-search-9f3c2a@example.test";
    private const string UserObjectId = "b7e1c0a2-4d55-4e8a-9c31-0a6f2e8d4b17";
    private const string SignedInObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string ProfileObjectId = "c8f2d1b3-5e66-4f9b-8d42-1b7a3f9e5c28";
    private const string ProfileEmail = "pii-profile-9f3c2a@example.test";

    [Fact]
    public async Task Admin_user_logs_omit_the_search_term_and_object_id()
    {
        var logs = new List<string>();
        var controller = new AdminUsersController(GraphConfiguration(), new ListLogger(logs), new HttpClient(new GraphStub()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = Principal(SignedInObjectId, "signed-in@example.test")
                }
            }
        };

        var search = Assert.IsType<ObjectResult>((await controller.SearchUsers(SearchTerm, CancellationToken.None)).Result);
        var grant = Assert.IsType<ObjectResult>((await controller.GrantAdminRole(Guid.Parse(UserObjectId), CancellationToken.None)).Result);
        var remove = Assert.IsType<ObjectResult>((await controller.RemoveAdminRole(Guid.Parse(UserObjectId), CancellationToken.None)).Result);

        var searchDetail = Assert.IsType<ProblemDetails>(search.Value).Detail;
        var grantDetail = Assert.IsType<ProblemDetails>(grant.Value).Detail;
        var removeDetail = Assert.IsType<ProblemDetails>(remove.Value).Detail;
        Assert.Equal("Admin user search failed.", searchDetail);
        Assert.Equal("Grant admin role failed.", grantDetail);
        Assert.Equal("Remove admin role failed.", removeDetail);
        Assert.DoesNotContain(SearchTerm, searchDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserObjectId, grantDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserObjectId, removeDetail, StringComparison.OrdinalIgnoreCase);

        var written = string.Join('\n', logs);
        Assert.DoesNotContain(SearchTerm, written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pii-search-9f3c2a", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserObjectId, written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"QueryEmpty=False QueryLength={SearchTerm.Length} ResultCount=0", written, StringComparison.Ordinal);
        Assert.Contains("Admin role grant failed.", written, StringComparison.Ordinal);
        Assert.Contains("Admin role removal failed.", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_save_logs_omit_the_object_id_and_email()
    {
        var logs = new List<string>();
        var database = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<PfohDbContext>().UseInMemoryDatabase(database).Options;
        await using (var seed = new PfohDbContext(options))
        {
            seed.UserProfiles.Add(new UserProfile
            {
                OwnerObjectId = ProfileObjectId,
                DisplayName = "Synthetic Profile User",
                Email = ProfileEmail,
                // Already filled, so sign-in provisioning does not write before the profile save.
                Phone = "+12145550199",
                Organization = "Synthetic Org",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new ThrowingProfileDb(options);
        var service = new UserProfileService(db, new ListLogger(logs), new ConfigurationBuilder().Build());
        var user = Principal(ProfileObjectId, ProfileEmail, phone: "2145550199", organization: "Synthetic Org");

        await service.GetOwnAsync(user, CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => service.UpdateOwnAsync(
            Principal(ProfileObjectId, ProfileEmail),
            ProfileObjectId,
            ProfileObjectId,
            "Synthetic Profile User",
            "214-555-0199",
            "Synthetic Org",
            CancellationToken.None));

        Assert.Contains(ProfileObjectId, conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ProfileEmail, conflict.Message, StringComparison.OrdinalIgnoreCase);
        var written = string.Join('\n', logs);
        Assert.DoesNotContain(ProfileObjectId, written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileEmail, written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pii-profile-9f3c2a", written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retrying profile save after a write conflict.", written, StringComparison.Ordinal);
    }

    private static IConfiguration GraphConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GraphAdmin:TenantId"] = "tenant-synthetic",
            ["GraphAdmin:ClientId"] = "client-synthetic",
            ["GraphAdmin:ClientSecret"] = "secret-synthetic",
            ["GraphAdmin:ResourceAppClientId"] = "resource-synthetic"
        })
        .Build();

    private static ClaimsPrincipal Principal(string objectId, string email, string? phone = null, string? organization = null)
    {
        var claims = new List<Claim>
        {
            new("oid", objectId),
            new("email", email),
            new("name", "Synthetic Profile User")
        };
        if (phone is not null)
        {
            claims.Add(new Claim("phone", phone));
        }

        if (organization is not null)
        {
            claims.Add(new Claim("organization", organization));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private sealed class ThrowingProfileDb(DbContextOptions<PfohDbContext> options) : PfohDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new DbUpdateException($"duplicate key value is ({ProfileObjectId}) email {ProfileEmail}");
    }

    private sealed class GraphStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"synthetic-token"}"""));
            }

            if (url.Contains("appRoleAssignedTo", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"value":[]}"""));
            }

            if (url.Contains("servicePrincipals", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """
                    {
                      "id": "11111111-2222-3333-4444-555555555555",
                      "appId": "resource-synthetic",
                      "displayName": "Synthetic API",
                      "appRoles": [
                        { "id": "66666666-7777-8888-9999-aaaaaaaaaaaa", "value": "PFOH.Admin", "isEnabled": true }
                      ]
                    }
                    """));
            }

            return Task.FromResult(Json(HttpStatusCode.BadRequest, url));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class ListLogger(List<string> logs) : ILogger<AdminUsersController>, ILogger<UserProfileService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = new StringBuilder();
            text.Append(formatter(state, exception));
            if (state is not string && state is System.Collections.IEnumerable items)
            {
                foreach (var item in items)
                {
                    text.Append(' ');
                    text.Append(item);
                }
            }

            if (exception is not null)
            {
                text.Append(' ');
                text.Append(exception);
            }

            logs.Add(text.ToString());
        }
    }
}
