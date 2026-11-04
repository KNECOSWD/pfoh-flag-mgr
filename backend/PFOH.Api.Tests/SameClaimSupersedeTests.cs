using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class SameClaimMatchTests
{
    private static readonly DateTime Noon = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_claim_without_a_grid_or_honoree_matches_only_the_same_owner()
    {
        var older = Ref(1, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 40, Noon);
        var newer = Ref(2, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 40, Noon.AddMinutes(1));
        var otherOwner = Ref(3, flagGridId: null, honoreeId: null, "owner-b", flagClaimId: 40, Noon.AddMinutes(2));
        var missingClaim = Ref(4, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 0, Noon);
        var missingClaimToo = Ref(5, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 0, Noon.AddMinutes(1));

        Assert.True(PendingReviewMaintenance.SameCard(older, newer));
        Assert.False(PendingReviewMaintenance.SameCard(older, otherOwner));
        Assert.False(PendingReviewMaintenance.SameCard(missingClaim, missingClaimToo));
    }

    [Fact]
    public void Different_owner_on_the_same_claim_does_not_supersede()
    {
        var owner = Ref(1, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 40, Noon);
        var other = Ref(2, flagGridId: null, honoreeId: null, "owner-b", flagClaimId: 40, Noon.AddMinutes(1));

        Assert.False(PendingReviewMaintenance.SameCard(owner, other));
        var kept = PendingReviewMaintenance.KeepNewest(new[] { owner, other }, row => row);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void KeepNewest_collapses_same_claim_rows()
    {
        var older = Ref(1, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 40, Noon);
        var newer = Ref(2, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 40, Noon.AddMinutes(5));
        var otherClaim = Ref(3, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 41, Noon.AddMinutes(6));

        var kept = PendingReviewMaintenance.KeepNewest(new[] { older, newer, otherClaim }, row => row);

        Assert.Equal(new[] { 2, 3 }, kept.Select(row => row.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void Grid_and_honoree_matching_is_unchanged()
    {
        var sameGrid = Ref(1, flagGridId: 9, honoreeId: null, "owner-a", flagClaimId: 1, Noon);
        var sameGridLater = Ref(2, flagGridId: 9, honoreeId: null, "owner-a", flagClaimId: 2, Noon.AddMinutes(1));
        var otherGrid = Ref(3, flagGridId: 8, honoreeId: null, "owner-a", flagClaimId: 3, Noon);
        var gridOtherOwner = Ref(9, flagGridId: 9, honoreeId: null, "owner-b", flagClaimId: 9, Noon.AddMinutes(2));
        var sameHonoree = Ref(4, flagGridId: null, honoreeId: 7, "owner-a", flagClaimId: 4, Noon);
        var sameHonoreeLater = Ref(5, flagGridId: null, honoreeId: 7, "owner-a", flagClaimId: 5, Noon.AddMinutes(1));
        var otherHonoree = Ref(6, flagGridId: null, honoreeId: 8, "owner-a", flagClaimId: 6, Noon);
        var honoreeOtherOwner = Ref(10, flagGridId: null, honoreeId: 7, "owner-b", flagClaimId: 10, Noon);
        var spotlessA = Ref(7, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 7, Noon);
        var spotlessB = Ref(8, flagGridId: null, honoreeId: null, "owner-a", flagClaimId: 8, Noon.AddMinutes(1));

        Assert.True(PendingReviewMaintenance.SameCard(sameGrid, sameGridLater));
        Assert.False(PendingReviewMaintenance.SameCard(sameGrid, otherGrid));
        Assert.False(PendingReviewMaintenance.SameCard(sameGrid, gridOtherOwner));
        Assert.True(PendingReviewMaintenance.SameCard(sameHonoree, sameHonoreeLater));
        Assert.False(PendingReviewMaintenance.SameCard(sameHonoree, otherHonoree));
        Assert.False(PendingReviewMaintenance.SameCard(sameHonoree, honoreeOtherOwner));
        Assert.False(PendingReviewMaintenance.SameCard(spotlessA, spotlessB));

        var keptGrid = PendingReviewMaintenance.KeepNewest(
            new[] { sameGrid, sameGridLater, otherGrid, gridOtherOwner },
            row => row);
        Assert.Equal(new[] { 2, 3, 9 }, keptGrid.Select(row => row.Id).OrderBy(id => id).ToArray());

        var keptSpotless = PendingReviewMaintenance.KeepNewest(new[] { spotlessA, spotlessB }, row => row);
        Assert.Equal(2, keptSpotless.Count);
    }

    [Fact]
    public void Supersede_does_not_read_the_duplicate_check_switch()
    {
        var maintenance = File.ReadAllText(SourcePath("backend/PFOH.Api/Services/PendingReviewMaintenance.cs"));
        Assert.DoesNotContain("SubmissionDuplicates", maintenance, StringComparison.Ordinal);

        var controller = File.ReadAllText(SourcePath("backend/PFOH.Api/Controllers/FlagClaimsController.cs"));
        var start = controller.IndexOf(
            "public async Task<ActionResult<FlagClaimDto>> SubmitClaim(",
            StringComparison.Ordinal);
        var end = controller.IndexOf("[Authorize(Policy = \"AdminOnly\")]", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var method = controller[start..end];
        Assert.DoesNotContain("submissions.Enabled", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SubmissionDuplicates", method, StringComparison.Ordinal);
        Assert.Contains("FinishSubmissionAsync(claim, latestDraft, ct)", method, StringComparison.Ordinal);
    }

    private static PendingReviewMaintenance.CardRef Ref(
        int id,
        int? flagGridId,
        int? honoreeId,
        string? ownerObjectId,
        int flagClaimId,
        DateTime submittedUtc) =>
        new(id, flagGridId, honoreeId, submittedUtc, submittedUtc, ownerObjectId, flagClaimId);

    private static string SourcePath(string relative)
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

public class SameClaimSubmitTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Same_claim_without_a_grid_or_honoree_supersedes_once_and_replay_keeps_one_receipt()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await SeedChangeAsync(501, 50, "submitter", "Submitted", now.AddMinutes(-8), "pending/older.jpg");
        await SeedChangeAsync(502, 50, "submitter", "Draft", now);
        await SeedChangeAsync(601, 60, "other-owner", "Submitted", now.AddMinutes(-3));
        factory.Files.Photos["pending/older.jpg"] = [8];
        var client = factory.CreateClient();

        var submit = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/50/submit",
            "submitter",
            "submitter@example.com",
            "Test Submitter"));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var created = await submit.Content.ReadFromJsonAsync<SubmittedClaim>(JsonOptions);
        Assert.NotNull(created?.ReceiptToken);
        Assert.NotEqual(Guid.Empty, created.ReceiptToken);

        var page = await client.GetAsync($"/api/submissions/{created.ReceiptToken}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var older = await db.HonoreeChangeRequests.SingleAsync(request => request.Id == 501);
            Assert.Equal("Superseded", older.RequestStatus);
            Assert.Equal("pending/older.jpg", older.PhotoFileName);
            Assert.DoesNotContain("pending/older.jpg", factory.Files.DeletedPendingPhotos);
            var keeper = await db.HonoreeChangeRequests.SingleAsync(request => request.Id == 502);
            Assert.Equal("Submitted", keeper.RequestStatus);
            var other = await db.HonoreeChangeRequests.SingleAsync(request => request.Id == 601);
            Assert.Equal("Submitted", other.RequestStatus);

            var audit = Assert.Single(await db.SubmissionAuditEvents.ToListAsync());
            Assert.Equal(SubmissionAuditEvent.Superseded, audit.Action);
            Assert.Equal(501, audit.ChangeRequestId);
            Assert.Equal(502, audit.KeeperChangeRequestId);

            var receipt = Assert.Single(await db.SubmissionReceipts.ToListAsync());
            Assert.Equal(502, receipt.ChangeRequestId);
            Assert.Equal(created.ReceiptToken, receipt.PublicToken);
        }

        var pending = await PendingIdsAsync(client);
        Assert.Contains(502, pending);
        Assert.Contains(601, pending);
        Assert.DoesNotContain(501, pending);

        var replay = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/50/submit",
            "submitter",
            "submitter@example.com",
            "Test Submitter"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<SubmittedClaim>(JsonOptions);
        Assert.Equal(created.ReceiptToken, replayed!.ReceiptToken);

        var untouched = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/60/submit",
            "other-owner",
            "other-owner@example.com",
            "Other Owner"));
        Assert.Equal(HttpStatusCode.BadRequest, untouched.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var receipt = Assert.Single(await db.SubmissionReceipts.ToListAsync());
            Assert.Equal(502, receipt.ChangeRequestId);
            Assert.Equal(created.ReceiptToken, receipt.PublicToken);
            var audit = Assert.Single(await db.SubmissionAuditEvents.ToListAsync());
            Assert.Equal(501, audit.ChangeRequestId);
            var other = await db.HonoreeChangeRequests.SingleAsync(request => request.Id == 601);
            Assert.Equal("Submitted", other.RequestStatus);
        }

        var restore = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/review/501/restore",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var restored = await db.HonoreeChangeRequests.SingleAsync(request => request.Id == 501);
            Assert.Equal("Submitted", restored.RequestStatus);
            Assert.Equal("pending/older.jpg", restored.PhotoFileName);
        }

        var back = await PendingIdsAsync(client);
        Assert.Contains(501, back);
        Assert.Contains(502, back);
        Assert.Contains(601, back);
    }

    [Fact]
    public async Task Pending_queue_hides_an_older_submitted_row_on_the_same_claim_without_rewriting_it()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await SeedChangeAsync(701, 70, "owner-a", "Submitted", now.AddMinutes(-10));
        await SeedChangeAsync(702, 70, "owner-a", "Submitted", now);
        await SeedChangeAsync(711, 71, "owner-b", "Submitted", now.AddMinutes(-1));

        var pending = await PendingIdsAsync(factory.CreateClient());

        Assert.Equal(new[] { 702, 711 }, pending.OrderBy(id => id).ToArray());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var statuses = await db.HonoreeChangeRequests
            .OrderBy(request => request.Id)
            .Select(request => request.RequestStatus)
            .ToListAsync();
        Assert.Equal(new[] { "Submitted", "Submitted", "Submitted" }, statuses);
        Assert.Empty(await db.SubmissionAuditEvents.ToListAsync());
        Assert.Empty(await db.SubmissionReceipts.ToListAsync());
    }

    [Fact]
    public async Task Different_owner_on_the_same_claim_is_not_superseded_by_maintenance()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await SeedChangeAsync(81, 80, "other-owner", "Submitted", now.AddMinutes(-5));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var maintenance = scope.ServiceProvider.GetRequiredService<PendingReviewMaintenance>();
        var keeper = new HonoreeChangeRequest
        {
            Id = 82,
            FlagClaimId = 80,
            FlagGridId = null,
            HonoreeId = null,
            FirstName = "Ada",
            LastName = "Lovelace",
            Description = "A newer submit from another account",
            RequestStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now,
            FlagClaim = new FlagClaim
            {
                ExternalUserObjectId = "submitter",
                ExternalUserEmail = "submitter@example.com",
                ClaimStatus = "Submitted",
                CreatedUtc = now
            }
        };

        await maintenance.SupersedeOlderPendingAsync(keeper, CancellationToken.None);

        db.ChangeTracker.Clear();
        var older = await db.HonoreeChangeRequests.SingleAsync(request => request.Id == 81);
        Assert.Equal("Submitted", older.RequestStatus);
        Assert.Empty(await db.SubmissionAuditEvents.ToListAsync());
    }

    private async Task<List<int>> PendingIdsAsync(HttpClient client)
    {
        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/pending",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = await response.Content.ReadFromJsonAsync<List<PendingItem>>(JsonOptions) ?? [];
        return pending.Select(item => item.ChangeRequestId).ToList();
    }

    private async Task ResetAsync()
    {
        factory.Files.Reset();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.SubmissionAuditEvents.RemoveRange(db.SubmissionAuditEvents);
        db.SubmissionReceipts.RemoveRange(db.SubmissionReceipts);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        await db.SaveChangesAsync();
    }

    private async Task SeedChangeAsync(
        int changeId,
        int claimId,
        string owner,
        string status,
        DateTime timestamp,
        string? photo = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        if (!await db.FlagClaims.AnyAsync(claim => claim.Id == claimId))
        {
            db.FlagClaims.Add(new FlagClaim
            {
                Id = claimId,
                FlagGridId = null,
                HonoreeId = null,
                ExternalUserObjectId = owner,
                ExternalUserEmail = $"{owner}@example.com",
                ExternalUserName = owner,
                ClaimStatus = status == "Draft" ? "Claimed" : "Submitted",
                CreatedUtc = timestamp,
                SubmittedUtc = status == "Submitted" ? timestamp : null
            });
        }

        db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            Id = changeId,
            FlagClaimId = claimId,
            FlagGridId = null,
            HonoreeId = null,
            FirstName = "Ada",
            LastName = "Lovelace",
            Description = "A tribute",
            PhotoFileName = photo,
            RequestStatus = status,
            CreatedUtc = timestamp,
            SubmittedUtc = status == "Submitted" ? timestamp : null
        });
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage Authed(
        HttpMethod method,
        string url,
        string ownerObjectId,
        string email,
        string name,
        string? roles = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, ownerObjectId);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        return request;
    }

    private sealed record SubmittedClaim(Guid? ReceiptToken);

    private sealed record PendingItem(int ChangeRequestId);
}
