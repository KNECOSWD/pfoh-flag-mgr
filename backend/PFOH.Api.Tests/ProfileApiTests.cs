using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
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
        Assert.Equal("", profile.Phone);
        Assert.Equal("", profile.Organization);
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
            new { displayName = "  Ada Lovelace  ", email = "evil@example.com", phone = "+1 (972) 555-0100", organization = "  Plano Flags  ", ownerObjectId = "user-ada" }));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var saved = await update.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(saved);
        Assert.Equal("Ada Lovelace", saved.DisplayName);
        Assert.Equal("ada@example.com", saved.Email);
        Assert.Equal("+19725550100", saved.Phone);
        Assert.Equal("Plano Flags", saved.Organization);
        Assert.True(saved.HasSavedDisplayName);

        var get = await client.SendAsync(ProfileRequest(HttpMethod.Get, "/api/profile", "user-ada", "ada@example.com", "unknown"));
        var loaded = await get.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(loaded);
        Assert.Equal("Ada Lovelace", loaded.DisplayName);
        Assert.Equal("ada@example.com", loaded.Email);
        Assert.Equal("+19725550100", loaded.Phone);
        Assert.Equal("Plano Flags", loaded.Organization);

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
            new { displayName = accepted, phone = "+19725550199" }));

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
            new { displayName = "Bob Volunteer", phone = "+19725550101", organization = "KNECO" }));
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

    [Fact]
    public async Task Get_maps_phone_and_organization_claims_and_ignores_a_missing_or_invalid_phone_claim()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var mapped = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "user-claims",
            "claims@example.com",
            "Pat Claims",
            phoneClaim: "+1 2145551234",
            organizationClaim: "First Responders",
            phoneClaimType: "phone",
            organizationClaimType: "organization"));
        var mappedProfile = await mapped.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(mappedProfile);
        Assert.Equal("+12145551234", mappedProfile.Phone);
        Assert.Equal("First Responders", mappedProfile.Organization);
        Assert.False(mappedProfile.HasSavedDisplayName);

        var extensionId = PFOH.Api.Extensions.EntraExtensionClaims.NormalizeAppId(
            PFOH.Api.Extensions.EntraExtensionClaims.DefaultExtensionsAppId);
        var extensionMapped = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "user-extension-claims",
            "extension-claims@example.com",
            "Pat Claims",
            phoneClaim: "+1 2145551234",
            organizationClaim: "Honor Guard",
            phoneClaimType: $"extension_{extensionId}_Phone",
            organizationClaimType: $"extension_{extensionId}_Organization"));
        var extensionProfile = await extensionMapped.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(extensionProfile);
        Assert.Equal("+12145551234", extensionProfile.Phone);
        Assert.Equal("Honor Guard", extensionProfile.Organization);

        var foreign = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "user-foreign-extension",
            "foreign-claims@example.com",
            "Pat Claims",
            phoneClaim: "+12145551234",
            organizationClaim: "Other",
            phoneClaimType: "extension_0123456789abcdef0123456789abcdef_Phone",
            organizationClaimType: "extension_0123456789abcdef0123456789abcdef_Organization"));
        var foreignProfile = await foreign.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(foreignProfile);
        Assert.Equal("", foreignProfile.Phone);
        Assert.Equal("", foreignProfile.Organization);

        var invalid = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "user-bad-phone",
            "bad-phone@example.com",
            "Pat Claims",
            phoneClaim: "+44 20 7946 0958"));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        var invalidProfile = await invalid.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(invalidProfile);
        Assert.Equal("", invalidProfile.Phone);
        Assert.True(invalidProfile.PhoneNeedsCorrection);
        Assert.Equal("", invalidProfile.Organization);

        var localFormat = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "user-local-phone",
            "local-phone@example.com",
            "Pat Claims",
            phoneClaim: "(214) 555-1234"));
        var localProfile = await localFormat.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(localProfile);
        Assert.Equal("+12145551234", localProfile.Phone);
        Assert.False(localProfile.PhoneNeedsCorrection);

        var missing = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "user-no-claims",
            "no-claims@example.com",
            "Pat Claims"));
        var missingProfile = await missing.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(missingProfile);
        Assert.Equal("", missingProfile.Phone);
        Assert.Equal("", missingProfile.Organization);
    }

    [Fact]
    public async Task Update_requires_a_us_phone_keeps_email_and_treats_organization_as_optional()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var missingPhone = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-phone",
            "phone@example.com",
            "Pat Phone",
            new { displayName = "Pat Phone", organization = "Station 1" }));
        Assert.Equal(HttpStatusCode.BadRequest, missingPhone.StatusCode);
        Assert.Equal(UsPhone.InvalidMessage, await ErrorMessageAsync(missingPhone));

        var badPhone = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-phone",
            "phone@example.com",
            "Pat Phone",
            new { displayName = "Pat Phone", phone = "214555123" }));
        Assert.Equal(HttpStatusCode.BadRequest, badPhone.StatusCode);
        Assert.Equal(UsPhone.InvalidMessage, await ErrorMessageAsync(badPhone));

        var nonUsPhone = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-phone",
            "phone@example.com",
            "Pat Phone",
            new { displayName = "Pat Phone", phone = "+44 20 7946 0958" }));
        Assert.Equal(HttpStatusCode.BadRequest, nonUsPhone.StatusCode);
        Assert.Equal(UsPhone.InvalidMessage, await ErrorMessageAsync(nonUsPhone));

        var letterPhone = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-phone",
            "phone@example.com",
            "Pat Phone",
            new { displayName = "Pat Phone", phone = "214-555-ABCD" }));
        Assert.Equal(HttpStatusCode.BadRequest, letterPhone.StatusCode);

        var missingEmail = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-phone-no-email",
            "",
            "Pat Phone",
            new { displayName = "Pat Phone", phone = "+19725550100" }));
        Assert.Equal(HttpStatusCode.BadRequest, missingEmail.StatusCode);

        var saved = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-phone",
            "phone@example.com",
            "Pat Phone",
            new { displayName = "Pat Phone", phone = "214-555-1234", email = "evil@example.com" }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var profile = await saved.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal("+12145551234", profile.Phone);
        Assert.False(profile.PhoneNeedsCorrection);
        Assert.Equal("phone@example.com", profile.Email);
        Assert.Equal("", profile.Organization);

        var tooLongOrganization = new string('O', UserProfileLimits.OrganizationMaxLength + 1);
        var longOrganization = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-phone",
            "phone@example.com",
            "Pat Phone",
            new { displayName = "Pat Phone", phone = "+19725550100", organization = tooLongOrganization }));
        Assert.Equal(HttpStatusCode.BadRequest, longOrganization.StatusCode);

        var cleared = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-phone",
            "phone@example.com",
            "Pat Phone",
            new { displayName = "Pat Phone", phone = "+19725550100", organization = "   " },
            organizationClaim: "Should Not Replace A Saved Blank"));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var clearedProfile = await cleared.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(clearedProfile);
        Assert.Equal("", clearedProfile.Organization);
    }

    [Fact]
    public async Task Saved_phone_wins_over_a_different_token_claim_and_blank_organization_is_filled_from_the_claim()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var save = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "user-fill",
            "fill@example.com",
            "Pat Fill",
            new { displayName = "Pat Fill", phone = "+19725550123" }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var filled = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "user-fill",
            "fill@example.com",
            "Pat Fill",
            phoneClaim: "+19725550999",
            organizationClaim: "Honor Guard",
            organizationClaimType: $"extension_{PFOH.Api.Extensions.EntraExtensionClaims.NormalizeAppId(PFOH.Api.Extensions.EntraExtensionClaims.DefaultExtensionsAppId)}_Organization"));
        var profile = await filled.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal("+19725550123", profile.Phone);
        Assert.Equal("Honor Guard", profile.Organization);

        var again = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "user-fill",
            "fill@example.com",
            "Pat Fill"));
        var persisted = await again.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(persisted);
        Assert.Equal("Honor Guard", persisted.Organization);
        Assert.Equal("+19725550123", persisted.Phone);
    }

    [Fact]
    public async Task Nominate_requires_an_account_email()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Ada"), "firstName" },
            { new StringContent("Lovelace"), "lastName" },
            { new StringContent("1"), "serviceBranchId" },
            { new StringContent("1"), "serviceBranchCategoryId" },
            { new StringContent("A tribute"), "description" },
            { new StringContent("+19725550100"), "submitterPhoneNumber" },
            { new StringContent("ada@example.com"), "submitterEmailAddress" }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/flag-claims/nominate")
        {
            Content = form
        };
        request.Headers.Add(TestAuthHandler.OidHeader, "user-nominate");
        request.Headers.Add(TestAuthHandler.NameHeader, "Ada Lovelace");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("nominate an honoree", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_sign_in_creates_one_row_and_a_second_sign_in_does_not()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var first = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "oid-once",
            "once@example.com",
            "Once Person",
            phoneClaim: "214-555-0191",
            organizationClaim: "Station 9"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "oid-once",
            "once@example.com",
            "Once Person",
            phoneClaim: "214-555-0191",
            organizationClaim: "Station 9"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var rows = await db.UserProfiles.Where(profile => profile.OwnerObjectId == "oid-once").ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("once@example.com", row.Email);
        Assert.Equal("+12145550191", row.Phone);
        Assert.Equal("Station 9", row.Organization);
        Assert.Equal(ProfileFieldSource.Claim, row.DisplayNameSource);
    }

    [Fact]
    public async Task Two_concurrent_first_requests_create_one_row()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        var left = client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "oid-concurrent",
            "concurrent@example.com",
            "Concurrent Person",
            phoneClaim: "214-555-0144",
            organizationClaim: "Gate"));
        var right = client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "oid-concurrent",
            "concurrent@example.com",
            "Concurrent Person",
            phoneClaim: "214-555-0144",
            organizationClaim: "Gate"));

        await Task.WhenAll(left, right);
        Assert.Equal(HttpStatusCode.OK, left.Result.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.Result.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var count = await db.UserProfiles.CountAsync(profile => profile.OwnerObjectId == "oid-concurrent");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Profile_form_values_survive_a_later_token()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var save = await client.SendAsync(ProfileRequest(
            HttpMethod.Put,
            "/api/profile",
            "oid-edited",
            "edited@example.com",
            "Token Name",
            new { displayName = "Edited Name", phone = "+12145550123", organization = "Edited Org" }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var later = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/profile",
            "oid-edited",
            "replacement@example.com",
            "Replacement Name",
            phoneClaim: "+12145550999",
            organizationClaim: "Replacement Org"));
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        var profile = await later.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal("Edited Name", profile.DisplayName);
        Assert.Equal("+12145550123", profile.Phone);
        Assert.Equal("Edited Org", profile.Organization);
        Assert.Equal("replacement@example.com", profile.Email);
        Assert.True(profile.HasSavedDisplayName);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var row = await db.UserProfiles.SingleAsync(item => item.OwnerObjectId == "oid-edited");
        Assert.Equal(ProfileFieldSource.Profile, row.DisplayNameSource);
        Assert.Equal(ProfileFieldSource.Profile, row.PhoneSource);
        Assert.Equal(ProfileFieldSource.Profile, row.OrganizationSource);
        Assert.Equal("Edited Name", row.DisplayName);
        Assert.Equal("+12145550123", row.Phone);
        Assert.Equal("Edited Org", row.Organization);
    }

    [Fact]
    public async Task Same_email_and_different_object_ids_are_different_profiles()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ProfileRequest(
            HttpMethod.Get, "/api/profile", "oid-alpha", "shared@example.com", "Alpha"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ProfileRequest(
            HttpMethod.Get, "/api/profile", "oid-beta", "shared@example.com", "Beta"))).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var ids = await db.UserProfiles
            .Where(profile => profile.Email == "shared@example.com")
            .Select(profile => profile.OwnerObjectId)
            .ToListAsync();
        Assert.Equal(2, ids.Count);
        Assert.Contains("oid-alpha", ids);
        Assert.Contains("oid-beta", ids);
    }

    [Fact]
    public async Task Admin_claimant_list_includes_saved_phone_and_organization()
    {
        await ResetAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 41,
                FlagGridId = 7,
                HonoreeId = 9,
                ExternalUserObjectId = "user-claimant",
                ExternalUserEmail = "claimant@example.com",
                ExternalUserName = "Casey Claimant",
                ClaimStatus = "Claimed",
                CreatedUtc = DateTime.UtcNow
            });
            db.UserProfiles.Add(new UserProfile
            {
                OwnerObjectId = "user-claimant",
                DisplayName = "Casey Claimant",
                Email = "claimant@example.com",
                Phone = "+19725550177",
                Organization = "Fire Station 3",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var response = await client.SendAsync(ProfileRequest(
            HttpMethod.Get,
            "/api/flag-claims/admin/honoree/9/claimants",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claimants = await response.Content.ReadFromJsonAsync<List<ClaimantResponse>>();
        Assert.NotNull(claimants);
        var claimant = Assert.Single(claimants);
        Assert.Equal("Casey Claimant", claimant.ClaimantName);
        Assert.Equal("+19725550177", claimant.ClaimantPhone);
        Assert.Equal("Fire Station 3", claimant.ClaimantOrganization);
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
        object? body = null,
        string? phoneClaim = null,
        string? organizationClaim = null,
        string? phoneClaimType = null,
        string? organizationClaimType = null,
        string? roles = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, ownerObjectId);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        if (phoneClaim is not null)
        {
            request.Headers.Add(TestAuthHandler.PhoneHeader, phoneClaim);
        }

        if (organizationClaim is not null)
        {
            request.Headers.Add(TestAuthHandler.OrganizationHeader, organizationClaim);
        }

        if (phoneClaimType is not null)
        {
            request.Headers.Add(TestAuthHandler.PhoneClaimTypeHeader, phoneClaimType);
        }

        if (organizationClaimType is not null)
        {
            request.Headers.Add(TestAuthHandler.OrganizationClaimTypeHeader, organizationClaimType);
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

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
        bool HasSavedDisplayName,
        string Phone,
        string Organization,
        bool PhoneNeedsCorrection = false);

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(payload);
        return payload["message"];
    }

    private sealed record ClaimantResponse(
        int ClaimId,
        string? ClaimantName,
        string ClaimantEmail,
        string? ClaimantPhone,
        string? ClaimantOrganization);
}

public class PfohApiFactory : WebApplicationFactory<Program>
{
    private readonly string databaseName = $"PfohProfileTests-{Guid.NewGuid():N}";

    public int? AuthenticatedGetPermit { get; set; }

    public Dictionary<string, string?>? ExtraSettings { get; set; }

    // The shared suite raises the permit so unrelated tests are not limited.
    // A rate-limit test sets this to keep the app default of 60.
    public bool UseRealAuthenticatedGetLimit { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>();
            if (!UseRealAuthenticatedGetLimit)
            {
                settings["RateLimit:AuthenticatedGetPermit"] = (AuthenticatedGetPermit ?? 100_000).ToString();
            }

            if (ExtraSettings is not null)
            {
                foreach (var pair in ExtraSettings)
                {
                    settings[pair.Key] = pair.Value;
                }
            }

            if (settings.Count > 0)
            {
                config.AddInMemoryCollection(settings);
            }
        });
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

            services.AddSingleton<ReprintClaimStatusFaultInterceptor>();
            services.AddSingleton<NominatePhotoSaveFaultInterceptor>();
            services.AddSingleton<ReactivationRedeemRaceInterceptor>();
            services.AddSingleton<ILoggerProvider, ReprintRemoveWarningLog>();
            services.AddDbContext<PfohDbContext>((sp, options) => options
                .UseInMemoryDatabase(databaseName)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .AddInterceptors(
                    sp.GetRequiredService<PublicFlagMapCacheInvalidator>(),
                    sp.GetRequiredService<ReprintClaimStatusFaultInterceptor>(),
                    sp.GetRequiredService<NominatePhotoSaveFaultInterceptor>(),
                    sp.GetRequiredService<ReactivationRedeemRaceInterceptor>()));

            services.RemoveAll<HonoreeFileStorage>();
            services.AddSingleton<HonoreeFileStorage, PendingReviewTestFileStorage>();

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

    public PendingReviewTestFileStorage Files =>
        Services.GetRequiredService<HonoreeFileStorage>() as PendingReviewTestFileStorage
        ?? throw new InvalidOperationException("Test file storage was not registered.");

    public sealed class PendingReviewTestFileStorage(IConfiguration configuration) : HonoreeFileStorage(configuration)
    {
        public Dictionary<string, byte[]> Photos { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> DeletedPendingPhotos { get; } = new();

        public List<string?> DownloadedImages { get; } = new();

        public Dictionary<int, byte[]> Pdfs { get; } = new();

        public HashSet<int> UnreadablePdfs { get; } = new();

        public override Task<byte[]?> DownloadPdfAsync(int honoreeId, CancellationToken ct)
        {
            if (UnreadablePdfs.Contains(honoreeId))
            {
                throw new IOException("Synthetic storage read failure.");
            }

            return Task.FromResult(Pdfs.TryGetValue(honoreeId, out var pdf) ? pdf : null);
        }

        public override Task<string> UploadPdfAsync(int honoreeId, byte[] pdf, CancellationToken ct)
        {
            Pdfs[honoreeId] = pdf;
            return Task.FromResult(GetHonoreePdfFileName(honoreeId));
        }

        public void Reset()
        {
            Pdfs.Clear();
            UnreadablePdfs.Clear();
            Photos.Clear();
            DeletedPendingPhotos.Clear();
            DownloadedImages.Clear();
        }

        public override async Task<string?> UploadPendingImageAsync(IFormFile? photo, int changeRequestId, CancellationToken ct)
        {
            if (photo is null || photo.Length == 0)
            {
                return null;
            }

            var extension = Path.GetExtension(photo.FileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            var fileName = $"pending/{changeRequestId}{extension.ToLowerInvariant()}";
            await using var stream = photo.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            Photos[fileName] = memory.ToArray();
            return fileName;
        }

        public override Task DeletePendingImageAsync(string? photoFileName, CancellationToken ct)
        {
            var name = HonoreeFileStorage.NormalizePendingName(photoFileName);
            if (name is null)
            {
                return Task.CompletedTask;
            }

            DeletedPendingPhotos.Add(name);
            Photos.Remove(name);
            return Task.CompletedTask;
        }

        public override Task<HonoreeImageContent?> OpenHonoreeImageAsync(string? photoFileName, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(photoFileName) && Photos.TryGetValue(photoFileName, out var bytes))
            {
                var contentType = photoFileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png"
                    : "image/jpeg";
                return Task.FromResult<HonoreeImageContent?>(new HonoreeImageContent(new MemoryStream(bytes), contentType));
            }

            return base.OpenHonoreeImageAsync(photoFileName, ct);
        }

        public override Task<byte[]?> DownloadImageAsync(string? photoFileName, string? fallbackImageUrl, CancellationToken ct)
        {
            DownloadedImages.Add(photoFileName);
            if (!string.IsNullOrWhiteSpace(photoFileName) && Photos.TryGetValue(photoFileName, out var bytes))
            {
                return Task.FromResult<byte[]?>(bytes);
            }

            return base.DownloadImageAsync(photoFileName, fallbackImageUrl, ct);
        }

        public override Task<string?> PromoteImageAsync(string? sourceFileName, int honoreeId, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(sourceFileName) && Photos.TryGetValue(sourceFileName, out var bytes))
            {
                var extension = Path.GetExtension(sourceFileName);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".jpg";
                }

                var finalFileName = $"{honoreeId}{extension.ToLowerInvariant()}";
                Photos[finalFileName] = bytes;
                if (HonoreeFileStorage.IsPendingPhotoName(sourceFileName))
                {
                    Photos.Remove(sourceFileName);
                }

                return Task.FromResult<string?>(finalFileName);
            }

            return base.PromoteImageAsync(sourceFileName, honoreeId, ct);
        }

        public Dictionary<string, byte[]> Logos { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override Task<byte[]?> DownloadServiceLogoAsync(string? fileName, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(fileName) && Logos.TryGetValue(fileName, out var bytes))
            {
                return Task.FromResult<byte[]?>(bytes);
            }

            return base.DownloadServiceLogoAsync(fileName, ct);
        }

        public override Task<HonoreeImageProbeResult> ProbeHonoreeImageAsync(string? photoFileName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(photoFileName))
            {
                return Task.FromResult(HonoreeImageProbeResult.NoPhoto);
            }

            if (Photos.ContainsKey(photoFileName))
            {
                return Task.FromResult(HonoreeImageProbeResult.Found);
            }

            if (photoFileName.Contains("missing-blob", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(HonoreeImageProbeResult.Missing);
            }

            if (photoFileName.Contains("explode-blob", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The honoree image blob could not be read.");
            }

            return Task.FromResult(HonoreeImageProbeResult.Found);
        }
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
    public const string RolesHeader = "X-Test-Roles";
    public const string PhoneHeader = "X-Test-Phone";
    public const string PhoneClaimTypeHeader = "X-Test-Phone-Claim-Type";
    public const string OrganizationHeader = "X-Test-Organization";
    public const string OrganizationClaimTypeHeader = "X-Test-Organization-Claim-Type";

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

        if (Request.Headers.TryGetValue(PhoneHeader, out var phoneValues) && !string.IsNullOrWhiteSpace(phoneValues.ToString()))
        {
            var phoneClaimType = Request.Headers.TryGetValue(PhoneClaimTypeHeader, out var phoneTypeValues) &&
                !string.IsNullOrWhiteSpace(phoneTypeValues.ToString())
                    ? phoneTypeValues.ToString()
                    : "phone_number";
            claims.Add(new Claim(phoneClaimType, phoneValues.ToString()));
        }

        if (Request.Headers.TryGetValue(OrganizationHeader, out var organizationValues) &&
            !string.IsNullOrWhiteSpace(organizationValues.ToString()))
        {
            var organizationClaimType = Request.Headers.TryGetValue(OrganizationClaimTypeHeader, out var organizationTypeValues) &&
                !string.IsNullOrWhiteSpace(organizationTypeValues.ToString())
                    ? organizationTypeValues.ToString()
                    : "organization";
            claims.Add(new Claim(organizationClaimType, organizationValues.ToString()));
        }

        if (Request.Headers.TryGetValue(RolesHeader, out var roleValues))
        {
            foreach (var role in roleValues.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

public static class ReprintClaimStatusFault
{
    public static bool RejectCancelled { get; set; }
}

public static class NominatePhotoSaveFault
{
    public static bool RejectPhotoFileNameUpdate { get; set; }
}

public sealed class NominatePhotoSaveFaultInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfRejected(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfRejected(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ThrowIfRejected(DbContext? context)
    {
        if (!NominatePhotoSaveFault.RejectPhotoFileNameUpdate || context is null)
        {
            return;
        }

        var rejected = context.ChangeTracker.Entries<HonoreeChangeRequest>().Any(entry =>
            entry.Property(change => change.PhotoFileName).IsModified);
        if (rejected)
        {
            throw new InvalidOperationException("Synthetic nominate photo save failure.");
        }
    }
}

public sealed class ReprintClaimStatusFaultInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfRejected(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfRejected(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ThrowIfRejected(DbContext? context)
    {
        if (!ReprintClaimStatusFault.RejectCancelled || context is null)
        {
            return;
        }

        var rejected = context.ChangeTracker.Entries<FlagClaim>().Any(entry =>
            entry.State == EntityState.Modified &&
            entry.Property(claim => claim.ClaimStatus).IsModified &&
            entry.Property(claim => claim.ClaimStatus).CurrentValue == "Cancelled");
        if (!rejected)
        {
            return;
        }

        throw new DbUpdateException(
            "Synthetic claim status rejection.",
            new InvalidOperationException("The UPDATE statement conflicted with the CHECK constraint \"CK_FlagClaims_ClaimStatus\"."));
    }
}

public sealed class ReprintRemoveWarningLog : ILoggerProvider, ILogger
{
    public static readonly List<string> Lines = new();

    public ILogger CreateLogger(string categoryName) => this;

    public void Dispose()
    {
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            Lines.Add(formatter(state, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
