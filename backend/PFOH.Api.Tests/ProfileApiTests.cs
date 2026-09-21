using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PFOH.Api.Data;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class ProfileApiTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    [Fact]
    public async Task Get_and_update_return_unauthorized_when_signed_out()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var getResponse = await client.GetAsync("/api/profile");
        var putResponse = await client.PutAsJsonAsync("/api/profile", new { displayName = "Ada Lovelace" });
        var putOtherResponse = await client.PutAsJsonAsync("/api/profile/someone-else", new { displayName = "Ada Lovelace" });

        Assert.Equal(HttpStatusCode.Unauthorized, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, putResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, putOtherResponse.StatusCode);
    }

    [Fact]
    public async Task Get_returns_the_signed_in_users_claims_before_a_profile_is_saved()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var response = await client.SendAsync(ProfileRequest(HttpMethod.Get, "/api/profile", "user-get", "get@example.com", "unknown"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal("user-get", profile.OwnerObjectId);
        Assert.Equal("", profile.DisplayName);
        Assert.Equal("get@example.com", profile.Email);
        Assert.False(profile.HasSavedDisplayName);
    }

    [Fact]
    public async Task Update_persists_the_callers_trimmed_display_name_and_keeps_email_read_only()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        await SeedClaimAsync("user-ada", "unknown");

        var update = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-ada",
            "ada@example.com",
            "unknown",
            new { displayName = "  Ada Lovelace  ", email = "evil@example.com", ownerObjectId = "user-ada" }));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var saved = await update.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(saved);
        Assert.Equal("Ada Lovelace", saved.DisplayName);
        Assert.Equal("ada@example.com", saved.Email);
        Assert.True(saved.HasSavedDisplayName);

        var get = await client.SendAsync(ProfileRequest(HttpMethod.Get, "/api/profile", "user-ada", "ada@example.com", "unknown"));
        var loaded = await get.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(loaded);
        Assert.Equal("Ada Lovelace", loaded.DisplayName);
        Assert.Equal("ada@example.com", loaded.Email);

        var claimName = await ClaimNameAsync("user-ada");
        Assert.Equal("Ada Lovelace", claimName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_rejects_an_empty_display_name(string displayName)
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var response = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-empty",
            "empty@example.com",
            "unknown",
            new { displayName }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var get = await client.SendAsync(ProfileRequest(HttpMethod.Get, "/api/profile", "user-empty", "empty@example.com", "Pat"));
        var profile = await get.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.False(profile.HasSavedDisplayName);
        Assert.Equal("Pat", profile.DisplayName);
    }

    [Fact]
    public async Task Update_rejects_a_display_name_over_the_length_limit()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        var tooLong = new string('A', UserProfileLimits.DisplayNameMaxLength + 1);

        var response = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-long",
            "long@example.com",
            "Pat",
            new { displayName = tooLong }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var accepted = new string('B', UserProfileLimits.DisplayNameMaxLength);
        var ok = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-long",
            "long@example.com",
            "Pat",
            new { displayName = accepted }));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var profile = await ok.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal(accepted, profile.DisplayName);
    }

    [Fact]
    public async Task Update_cannot_change_another_users_profile()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        await SeedClaimAsync("user-bob", "unknown");

        var saveBob = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-bob",
            "bob@example.com",
            "unknown",
            new { displayName = "Bob Volunteer" }));
        Assert.Equal(HttpStatusCode.OK, saveBob.StatusCode);

        var byRoute = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile/user-bob",
            "user-ada",
            "ada@example.com",
            "Ada Lovelace",
            new { displayName = "Hijacked" }));
        var byBody = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-ada",
            "ada@example.com",
            "Ada Lovelace",
            new { displayName = "Hijacked", ownerObjectId = "user-bob" }));

        Assert.Equal(HttpStatusCode.Forbidden, byRoute.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byBody.StatusCode);

        var bob = await client.SendAsync(ProfileRequest(HttpMethod.Get, "/api/profile", "user-bob", "bob@example.com", "unknown"));
        var bobProfile = await bob.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(bobProfile);
        Assert.Equal("Bob Volunteer", bobProfile.DisplayName);
        Assert.Equal("bob@example.com", bobProfile.Email);
        Assert.Equal("Bob Volunteer", await ClaimNameAsync("user-bob"));

        var ada = await client.SendAsync(ProfileRequest(HttpMethod.Get, "/api/profile", "user-ada", "ada@example.com", "Ada Lovelace"));
        var adaProfile = await ada.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(adaProfile);
        Assert.False(adaProfile.HasSavedDisplayName);
        Assert.Equal("Ada Lovelace", adaProfile.DisplayName);
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.RemoveRange(db.UserProfiles);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        await db.SaveChangesAsync();
    }

    private async Task SeedClaimAsync(string ownerObjectId, string displayName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.FlagClaims.Add(new FlagClaim
        {
            FlagGridId = 1,
            ExternalUserObjectId = ownerObjectId,
            ExternalUserEmail = $"{ownerObjectId}@example.com",
            ExternalUserName = displayName,
            ClaimStatus = "Claimed",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> ClaimNameAsync(string ownerObjectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        return await db.FlagClaims
            .Where(claim => claim.ExternalUserObjectId == ownerObjectId)
            .Select(claim => claim.ExternalUserName)
            .FirstAsync();
    }

    private static HttpRequestMessage ProfileRequest(
        HttpMethod method,
        string url,
        string ownerObjectId,
        string email,
        string name,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, ownerObjectId);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private sealed record UserProfileResponse(
        string OwnerObjectId,
        string DisplayName,
        string Email,
        bool HasSavedDisplayName);
}

public sealed class PfohApiFactory : WebApplicationFactory<Program>
{
    private readonly string databaseName = $"PfohProfileTests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            var descriptors = services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(DbContextOptions<PfohDbContext>) ||
                    descriptor.ServiceType == typeof(PfohDbContext))
                .ToList();

            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<PfohDbContext>(options => options.UseInMemoryDatabase(databaseName));

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}

public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string OidHeader = "X-Test-Oid";
    public const string EmailHeader = "X-Test-Email";
    public const string NameHeader = "X-Test-Name";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(OidHeader, out var oidValues) || string.IsNullOrWhiteSpace(oidValues.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var oid = oidValues.ToString();
        var claims = new List<Claim>
        {
            new("oid", oid),
            new(ClaimTypes.NameIdentifier, oid)
        };

        if (Request.Headers.TryGetValue(EmailHeader, out var emailValues) && !string.IsNullOrWhiteSpace(emailValues.ToString()))
        {
            claims.Add(new Claim("preferred_username", emailValues.ToString()));
            claims.Add(new Claim(ClaimTypes.Email, emailValues.ToString()));
        }

        if (Request.Headers.TryGetValue(NameHeader, out var nameValues) && !string.IsNullOrWhiteSpace(nameValues.ToString()))
        {
            claims.Add(new Claim("name", nameValues.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
