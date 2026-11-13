using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class HonoreeBioLengthTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private const string Owner = "bio-owner";
    private const string OwnerEmail = "bio-owner@example.test";

    [Fact]
    public void Description_max_length_lives_on_the_shared_write_dto()
    {
        var property = typeof(SaveHonoreeChangeRequest).GetProperty(nameof(SaveHonoreeChangeRequest.Description));
        Assert.NotNull(property);
        var max = property!.GetCustomAttribute<MaxLengthAttribute>();
        Assert.NotNull(max);
        Assert.Equal(HonoreeBioLimits.MaxLength, max!.Length);
        Assert.Equal(HonoreeBioLimits.TooLongMessage, max.ErrorMessage);
        Assert.Contains(HonoreeBioLimits.MaxLength.ToString(), HonoreeBioLimits.TooLongMessage, StringComparison.Ordinal);
        Assert.NotNull(property.GetCustomAttribute<RequiredAttribute>());
        Assert.Equal(typeof(SaveHonoreeChangeRequest), typeof(NominateHonoreeRequest).BaseType);

        var frontend = File.ReadAllText(RepoFile("frontend/src/honoreeBio.ts"));
        Assert.Contains($"export const HONOREE_BIO_MAX_LENGTH = {HonoreeBioLimits.MaxLength};", frontend, StringComparison.Ordinal);
        var sql = File.ReadAllText(RepoFile("docs/sql/bio-over-limit-count.sql"));
        Assert.Contains("DATALENGTH(Description) / 2", sql, StringComparison.Ordinal);
        Assert.Contains("LEN ignores trailing spaces", sql, StringComparison.Ordinal);
        Assert.Contains($"@MaxBio int = {HonoreeBioLimits.MaxLength}", sql, StringComparison.Ordinal);
        Assert.Contains("RequestStatus = N'Submitted'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nominate_accepts_a_bio_at_the_limit_and_rejects_one_past_it()
    {
        await ResetAsync();
        await SeedSponsorCategoryAsync();
        var client = factory.CreateClient();

        var accepted = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            Owner,
            OwnerEmail,
            "Bio Owner",
            body: Form("Nora", "Limit", Bio(HonoreeBioLimits.MaxLength), "submit-new")));
        var acceptedBody = await accepted.Content.ReadAsStringAsync();
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, acceptedBody);

        var rejected = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            Owner,
            OwnerEmail,
            "Bio Owner",
            body: Form("Nora", "Over", Bio(HonoreeBioLimits.MaxLength + 1), "submit-new")));
        await AssertBioRejected(rejected);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var stored = await db.HonoreeChangeRequests.SingleAsync(row => row.LastName == "Limit");
        Assert.Equal(HonoreeBioLimits.MaxLength, stored.Description!.Length);
        Assert.DoesNotContain(await db.HonoreeChangeRequests.Select(row => row.LastName).ToListAsync(), name => name == "Over");
    }

    [Fact]
    public async Task Update_draft_accepts_a_bio_at_the_limit_and_rejects_one_past_it()
    {
        await ResetAsync();
        var claimId = await SeedClaimAsync("Claimed", "Short tribute.");
        var client = factory.CreateClient();

        var accepted = await client.SendAsync(Authed(
            HttpMethod.Put,
            $"/api/flag-claims/{claimId}/honoree-draft",
            Owner,
            OwnerEmail,
            "Bio Owner",
            body: Form("Ida", "Draft", Bio(HonoreeBioLimits.MaxLength))));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var rejected = await client.SendAsync(Authed(
            HttpMethod.Put,
            $"/api/flag-claims/{claimId}/honoree-draft",
            Owner,
            OwnerEmail,
            "Bio Owner",
            body: Form("Ida", "Draft", Bio(HonoreeBioLimits.MaxLength + 1), rank: "Colonel")));
        await AssertBioRejected(rejected);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var draft = await db.HonoreeChangeRequests.SingleAsync(row => row.FlagClaimId == claimId && row.RequestStatus == "Draft");
        Assert.Equal(HonoreeBioLimits.MaxLength, draft.Description!.Length);
        Assert.NotEqual("Colonel", draft.Rank);
    }

    [Fact]
    public async Task Admin_apply_fields_accepts_a_bio_at_the_limit_and_rejects_one_past_it()
    {
        await ResetAsync();
        var honoreeId = await SeedHonoreeAsync("Kept rank.", "AA-18");
        var client = factory.CreateClient();

        var rejected = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/flag-claims/admin/honoree/{honoreeId}/apply-fields",
            "admin-1",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: Form("Grace", "Hopper", Bio(HonoreeBioLimits.MaxLength + 40), rank: "Admiral")));
        await AssertBioRejected(rejected);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var unchanged = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
            Assert.Equal("Kept rank.", unchanged.Description);
            Assert.Equal("Captain", unchanged.Rank);
        }

        var accepted = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/flag-claims/admin/honoree/{honoreeId}/apply-fields",
            "admin-1",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: Form("Grace", "Hopper", Bio(HonoreeBioLimits.MaxLength), rank: "Admiral")));
        var acceptedBody = await accepted.Content.ReadAsStringAsync();
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, acceptedBody);

        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await saved.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.Equal(HonoreeBioLimits.MaxLength, honoree.Description.Length);
        Assert.Equal("Admiral", honoree.Rank);
    }

    [Fact]
    public async Task Pending_apply_accepts_a_bio_at_the_limit_and_rejects_one_past_it()
    {
        await ResetAsync();
        var changeId = await SeedPendingAsync("Pending tribute.");
        var client = factory.CreateClient();

        var rejected = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/flag-claims/pending/{changeId}/apply",
            Owner,
            OwnerEmail,
            "Bio Owner",
            body: Form("Paul", "Pending", Bio(HonoreeBioLimits.MaxLength + 1))));
        await AssertBioRejected(rejected);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var unchanged = await db.HonoreeChangeRequests.SingleAsync(row => row.Id == changeId);
            Assert.Equal("Pending tribute.", unchanged.Description);
        }

        var accepted = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/flag-claims/pending/{changeId}/apply",
            Owner,
            OwnerEmail,
            "Bio Owner",
            body: Form("Paul", "Pending", Bio(HonoreeBioLimits.MaxLength))));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var change = await saved.HonoreeChangeRequests.SingleAsync(row => row.Id == changeId);
        Assert.Equal(HonoreeBioLimits.MaxLength, change.Description!.Length);
        Assert.Equal("Submitted", change.RequestStatus);
    }

    [Fact]
    public async Task Admin_edit_shows_an_existing_over_limit_bio_and_will_not_save_until_it_is_shortened()
    {
        await ResetAsync();
        var existing = Bio(HonoreeBioLimits.MaxLength + 80);
        var honoreeId = await SeedHonoreeAsync(existing, "AA-22");
        var client = factory.CreateClient();

        var opened = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/flag-claims/admin/honoree/{honoreeId}/edit",
            "admin-1",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin"));
        var openedBody = await opened.Content.ReadAsStringAsync();
        Assert.True(opened.StatusCode == HttpStatusCode.OK, openedBody);
        using var openedJson = JsonDocument.Parse(openedBody);
        var shown = openedJson.RootElement.GetProperty("latestChangeRequest").GetProperty("description").GetString();
        Assert.Equal(existing, shown);
        var claimId = openedJson.RootElement.GetProperty("id").GetInt32();

        var blocked = await client.SendAsync(Authed(
            HttpMethod.Put,
            $"/api/flag-claims/{claimId}/honoree-draft",
            "admin-1",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: Form("Grace", "Hopper", existing, rank: "Admiral")));
        await AssertBioRejected(blocked);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
            Assert.Equal(existing, honoree.Description);
            Assert.Equal("Captain", honoree.Rank);
            var draft = await db.HonoreeChangeRequests.SingleAsync(row => row.FlagClaimId == claimId && row.RequestStatus == "Draft");
            Assert.Equal(existing, draft.Description);
            Assert.Equal("Captain", draft.Rank);
        }

        var shortened = Bio(HonoreeBioLimits.MaxLength);
        var saved = await client.SendAsync(Authed(
            HttpMethod.Put,
            $"/api/flag-claims/{claimId}/honoree-draft",
            "admin-1",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: Form("Grace", "Hopper", shortened, rank: "Admiral")));
        var savedBody = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.StatusCode == HttpStatusCode.OK, savedBody);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var draftAfter = await after.HonoreeChangeRequests.SingleAsync(row => row.FlagClaimId == claimId && row.RequestStatus == "Draft");
        Assert.Equal(shortened, draftAfter.Description);
        Assert.Equal("Admiral", draftAfter.Rank);
        var honoreeAfter = await after.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.Equal(existing, honoreeAfter.Description);
    }

    [Fact]
    public async Task Approving_a_pending_request_with_an_over_limit_bio_keeps_the_full_text()
    {
        await ResetAsync();
        var existing = Bio(HonoreeBioLimits.MaxLength + 120);
        var honoreeId = await SeedHonoreeAsync("Current tribute.", "AA-30");
        var changeId = await SeedPendingAsync(existing, honoreeId);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{changeId}/approve",
            "admin-1",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: JsonContent.Create(new { requiresCardReprint = false, reviewNotes = "Keep the stored bio." })));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
        var change = await db.HonoreeChangeRequests.SingleAsync(row => row.Id == changeId);
        Assert.Equal("Approved", change.RequestStatus);
        Assert.Equal(existing, change.Description);
        Assert.Equal(existing, honoree.Description);
    }

    private static async Task AssertBioRejected(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains(HonoreeBioLimits.TooLongMessage, body, StringComparison.Ordinal);
    }

    private static string Bio(int length) => new('W', length);

    private static MultipartFormDataContent Form(
        string first,
        string last,
        string description,
        string? duplicateAction = null,
        string? rank = null)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(first), "FirstName" },
            { new StringContent(last), "LastName" },
            { new StringContent(rank ?? "Captain"), "Rank" },
            { new StringContent("1"), "ServiceBranchId" },
            { new StringContent("1"), "ServiceBranchCategoryId" },
            { new StringContent(description), "Description" },
            { new StringContent("2145550148"), "SubmitterPhoneNumber" },
            { new StringContent("family@example.test"), "SubmitterEmailAddress" },
            { new StringContent("false"), "Kia" }
        };
        if (duplicateAction is not null)
        {
            content.Add(new StringContent(duplicateAction), "DuplicateAction");
        }

        return content;
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.Sponsors.RemoveRange(db.Sponsors);
        db.SponsorCategories.RemoveRange(db.SponsorCategories);
        await db.SaveChangesAsync();
    }

    private async Task SeedSponsorCategoryAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        db.SponsorCategories.Add(new SponsorCategory
        {
            SponsorCategoryName = "Family",
            Description = "Family",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedHonoreeAsync(string description, string gridName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        var grid = new FlagGrid
        {
            FlagGridName = gridName,
            Notes = string.Empty,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.FlagGrids.Add(grid);
        await db.SaveChangesAsync();
        var honoree = new Honoree
        {
            FlagGridId = grid.Id,
            FirstName = "Grace",
            LastName = "Hopper",
            Rank = "Captain",
            Description = description,
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = string.Empty,
            PhoneNumber = string.Empty,
            EmailAddress = string.Empty,
            Awards = string.Empty,
            ConflictsServed = string.Empty,
            DatesUserEntry = string.Empty,
            IsActive = true,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.Honorees.Add(honoree);
        grid.HonoreeId = 0;
        await db.SaveChangesAsync();
        grid.HonoreeId = honoree.Id;
        await db.SaveChangesAsync();
        return honoree.Id;
    }

    private async Task<int> SeedClaimAsync(string status, string description)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        var claim = new FlagClaim
        {
            ExternalUserObjectId = Owner,
            ExternalUserEmail = OwnerEmail,
            ExternalUserName = "Bio Owner",
            ClaimStatus = status,
            CreatedUtc = now
        };
        claim.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            FirstName = "Ida",
            LastName = "Draft",
            Rank = "Captain",
            Description = description,
            RequestStatus = "Draft",
            CreatedUtc = now,
            SubmitterPhoneNumber = "2145550148",
            SubmitterEmailAddress = OwnerEmail
        });
        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync();
        return claim.Id;
    }

    private async Task<int> SeedPendingAsync(string description, int? honoreeId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        var claim = new FlagClaim
        {
            HonoreeId = honoreeId,
            ExternalUserObjectId = Owner,
            ExternalUserEmail = OwnerEmail,
            ExternalUserName = "Bio Owner",
            ClaimStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        };
        var change = new HonoreeChangeRequest
        {
            HonoreeId = honoreeId,
            FirstName = "Paul",
            LastName = "Pending",
            Rank = "Captain",
            Description = description,
            RequestStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now,
            SubmitterPhoneNumber = "2145550148",
            SubmitterEmailAddress = OwnerEmail
        };
        claim.HonoreeChangeRequests.Add(change);
        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync();
        return change.Id;
    }

    private static HttpRequestMessage Authed(
        HttpMethod method,
        string url,
        string ownerObjectId,
        string email,
        string name,
        string? roles = null,
        HttpContent? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, ownerObjectId);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        if (body is not null)
        {
            request.Content = body;
        }

        return request;
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
