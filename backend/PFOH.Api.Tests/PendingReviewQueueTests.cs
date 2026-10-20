using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class ReviewSubmissionDiffTests
{
    [Fact]
    public void Compare_reports_rank_and_ignores_blank_versus_null()
    {
        var current = Snapshot(honoreeId: 9, rank: "Private", middleName: null, photo: "photos/current.jpg");
        var proposed = Snapshot(honoreeId: 9, rank: "Sergeant", middleName: "   ", photo: "pending/9.jpg");

        var changes = ReviewSubmissionDiff.Compare(proposed, current);

        var rank = Assert.Single(changes, change => change.Label == "Rank");
        Assert.Equal("Private", rank.Previous);
        Assert.Equal("Sergeant", rank.Proposed);
        Assert.DoesNotContain(changes, change => change.Label == "Middle name");
        var photo = Assert.Single(changes, change => change.Label == "Photo");
        Assert.Equal("Current photo", photo.Previous);
        Assert.Equal("New photo", photo.Proposed);
    }

    [Fact]
    public void Compare_returns_no_field_changes_for_a_new_submission()
    {
        var proposed = Snapshot(honoreeId: null, rank: "Private", middleName: null, photo: null);

        Assert.Empty(ReviewSubmissionDiff.Compare(proposed, current: null));
        Assert.Equal(ReviewSubmissionDiff.NewSubmission, ReviewSubmissionDiff.Kind(null));
        Assert.Equal(ReviewSubmissionDiff.Change, ReviewSubmissionDiff.Kind(9));
    }

    private static ReviewSnapshot Snapshot(int? honoreeId, string? rank, string? middleName, string? photo) => new(
        honoreeId,
        "Ada",
        middleName,
        "Lovelace",
        null,
        null,
        rank,
        null,
        null,
        null,
        null,
        null,
        "A tribute",
        false,
        photo);
}

public class PendingReviewQueueTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task New_submission_with_a_photo_has_no_spot_and_shows_on_my_submissions_and_review()
    {
        await ResetAsync();
        await SeedCatalogAsync();
        var photo = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var client = factory.CreateClient();

        var nominate = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "submitter",
            "submitter@example.com",
            "Test Submitter",
            body: Nomination(photo)));

        Assert.Equal(HttpStatusCode.OK, nominate.StatusCode);
        var created = await nominate.Content.ReadFromJsonAsync<ClaimResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Null(created.FlagGridId);
        Assert.Null(created.HonoreeImageUrl);
        Assert.NotNull(created.LatestChangeRequest);
        Assert.StartsWith("pending/", created.LatestChangeRequest.PhotoFileName, StringComparison.Ordinal);

        var mine = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/flag-claims/my",
            "submitter",
            "submitter@example.com",
            "Test Submitter"));
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var claims = await mine.Content.ReadFromJsonAsync<List<ClaimResponse>>(JsonOptions);
        var claim = Assert.Single(claims!);
        Assert.Null(claim.FlagGridId);
        Assert.Equal(created.LatestChangeRequest.PhotoFileName, claim.LatestChangeRequest?.PhotoFileName);

        var photoResponse = await client.SendAsync(Authed(
            HttpMethod.Get,
            $"/api/flagclaims/change-requests/{created.LatestChangeRequest.Id}/photo",
            "submitter",
            "submitter@example.com",
            "Test Submitter"));
        Assert.Equal(HttpStatusCode.OK, photoResponse.StatusCode);
        Assert.Equal(photo, await photoResponse.Content.ReadAsByteArrayAsync());

        var pending = await PendingAsync(client);
        var item = Assert.Single(pending);
        Assert.Equal(created.LatestChangeRequest.PhotoFileName, item.PhotoFileName);
        Assert.Equal("New", item.SubmissionKind);
        Assert.Null(item.FlagGridId);
        Assert.Equal("Not assigned", item.FlagGridName is { Length: > 0 } name ? name : "Not assigned");
    }

    [Fact]
    public async Task Approving_a_new_submission_assigns_the_first_available_spot_by_name()
    {
        await ResetAsync();
        await SeedCatalogAsync();
        await SeedGridAsync(1, "A-01");
        await SeedGridAsync(2, "C-28");
        var (changeId, _) = await SeedNewSubmissionAsync(flagGridId: 1);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{changeId}/approve",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin",
            body: new { requiresCardReprint = false, reviewNotes = "Approved" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = await response.Content.ReadFromJsonAsync<ApproveResponse>(JsonOptions);
        Assert.NotNull(approved);
        Assert.True(approved.FlagSpotAssigned);
        Assert.Equal("A-01", approved.FlagGridName);
        Assert.Equal(1, approved.FlagGridId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync();
        Assert.Equal(1, honoree.FlagGridId);
        var change = await db.HonoreeChangeRequests.SingleAsync(request => request.Id == changeId);
        Assert.Equal("Approved", change.RequestStatus);
    }

    [Fact]
    public async Task Approving_a_new_submission_with_no_open_spot_still_succeeds()
    {
        await ResetAsync();
        await SeedCatalogAsync();
        var (changeId, _) = await SeedNewSubmissionAsync(flagGridId: null);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{changeId}/approve",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin",
            body: new { requiresCardReprint = false, reviewNotes = "Approved" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = await response.Content.ReadFromJsonAsync<ApproveResponse>(JsonOptions);
        Assert.NotNull(approved);
        Assert.False(approved.FlagSpotAssigned);
        Assert.Contains("no flag spot is available", approved.FlagSpotNotice, StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync();
        Assert.Null(honoree.FlagGridId);
        Assert.Equal("Approved", await db.HonoreeChangeRequests.Where(request => request.Id == changeId).Select(request => request.RequestStatus).SingleAsync());
    }

    [Fact]
    public async Task Pending_queue_keeps_one_request_per_card_and_cleanup_supersedes_the_older_ones()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await SeedSubmittedAsync(1, 11, 31, now.AddMinutes(-30), "pending/1.jpg", honoreeId: null);
        await SeedSubmittedAsync(2, 12, 31, now.AddMinutes(-20), "pending/2.jpg", honoreeId: null);
        await SeedSubmittedAsync(3, 13, 31, now.AddMinutes(-10), "pending/3.jpg", honoreeId: null);
        factory.Files.Photos["pending/1.jpg"] = [1];
        factory.Files.Photos["pending/2.jpg"] = [2];
        factory.Files.Photos["pending/3.jpg"] = [3];
        var client = factory.CreateClient();

        var beforeCleanup = await PendingAsync(client);
        var visible = Assert.Single(beforeCleanup);
        Assert.Equal(3, visible.ChangeRequestId);
        Assert.Empty(factory.Files.DeletedPendingPhotos);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<PendingReviewMaintenance>();
            await maintenance.CleanupDuplicatePendingAsync(CancellationToken.None);
        }

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var requests = await db.HonoreeChangeRequests.OrderBy(request => request.Id).ToListAsync();
        Assert.Equal("Superseded", requests[0].RequestStatus);
        Assert.Equal("Superseded", requests[1].RequestStatus);
        Assert.Equal("Submitted", requests[2].RequestStatus);
        Assert.Contains("Change request 3", requests[0].ReviewNotes, StringComparison.Ordinal);
        Assert.Null(requests[0].PhotoFileName);
        Assert.Null(requests[1].PhotoFileName);
        Assert.Equal("pending/3.jpg", requests[2].PhotoFileName);
        Assert.Contains("pending/1.jpg", factory.Files.DeletedPendingPhotos);
        Assert.Contains("pending/2.jpg", factory.Files.DeletedPendingPhotos);
        Assert.DoesNotContain("pending/3.jpg", factory.Files.DeletedPendingPhotos);
    }

    [Fact]
    public async Task Submitting_a_newer_draft_supersedes_the_older_pending_request_for_the_same_card()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await SeedSubmittedAsync(8, 18, 40, now.AddMinutes(-5), "pending/8.jpg", honoreeId: 4, owner: "submitter");
        await SeedDraftAsync(9, 18, 40, now, honoreeId: 4, owner: "submitter");
        factory.Files.Photos["pending/8.jpg"] = [8];
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/18/submit",
            "submitter",
            "submitter@example.com",
            "Test Submitter"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = await PendingAsync(client);
        var visible = Assert.Single(pending);
        Assert.Equal(9, visible.ChangeRequestId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var older = await db.HonoreeChangeRequests.SingleAsync(request => request.Id == 8);
        Assert.Equal("Superseded", older.RequestStatus);
        Assert.Null(older.PhotoFileName);
        Assert.Contains("pending/8.jpg", factory.Files.DeletedPendingPhotos);
    }

    [Fact]
    public async Task Distinct_new_submissions_without_a_card_all_stay_in_the_queue()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await SeedSubmittedAsync(21, 41, flagGridId: null, now.AddMinutes(-2), photo: null, honoreeId: null);
        await SeedSubmittedAsync(22, 42, flagGridId: null, now.AddMinutes(-1), photo: null, honoreeId: null);
        await SeedSubmittedAsync(23, 43, flagGridId: null, now, photo: null, honoreeId: null);

        var pending = await PendingAsync(factory.CreateClient());

        Assert.Equal(3, pending.Count);
    }

    [Fact]
    public async Task Withdraw_sets_withdrawn_deletes_the_pending_photo_and_leaves_the_queue()
    {
        await ResetAsync();
        await SeedSubmittedAsync(15, 4, 7, DateTime.UtcNow, "pending/15.jpg", honoreeId: null, owner: "owner-photo");
        factory.Files.Photos["pending/15.jpg"] = [15];
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/4/unclaim",
            "owner-photo",
            "owner@example.com",
            "Olivia Owner"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await PendingAsync(client));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var change = await db.HonoreeChangeRequests.SingleAsync();
        Assert.Equal("Withdrawn", change.RequestStatus);
        Assert.Null(change.PhotoFileName);
        Assert.Null(change.FlagGridId);
        Assert.Contains("pending/15.jpg", factory.Files.DeletedPendingPhotos);
        Assert.DoesNotContain(await db.HonoreeChangeRequests.Select(request => request.RequestStatus).ToListAsync(), status => status == "Withdrawal");
    }

    [Fact]
    public async Task Approved_withdraw_queues_a_withdrawal_and_keeps_the_spot_until_an_admin_confirms()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagGrids.Add(new FlagGrid
            {
                Id = 33,
                FlagGridName = "V-33",
                HonoreeId = 8,
                Notes = string.Empty,
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now
            });
            db.Honorees.Add(new Honoree
            {
                Id = 8,
                FlagGridId = 33,
                FirstName = "Hulett",
                MiddleName = "H.",
                LastName = "Abbott",
                Suffix = "V",
                Rank = "Sergeant",
                Description = "A tribute",
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
            });
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 11,
                FlagGridId = 33,
                HonoreeId = 8,
                ExternalUserObjectId = "submitter",
                ExternalUserEmail = "submitter@example.com",
                ExternalUserName = "Hulett Submitter",
                ClaimStatus = "Claimed",
                CreatedUtc = now,
                SubmittedUtc = now,
                ApprovedUtc = now
            });
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                Id = 41,
                FlagClaimId = 11,
                FlagGridId = 33,
                HonoreeId = 8,
                FirstName = "Hulett",
                LastName = "Abbott",
                Description = "A tribute",
                RequestStatus = "Approved",
                CreatedUtc = now,
                SubmittedUtc = now,
                ReviewedUtc = now
            });
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 90,
                FlagGridId = null,
                ClaimStatus = "Submitted",
                ExternalUserObjectId = "spotless",
                ExternalUserEmail = "spotless@example.com",
                CreatedUtc = now
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var withdraw = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/11/unclaim",
            "submitter",
            "submitter@example.com",
            "Hulett Submitter"));
        Assert.Equal(HttpStatusCode.OK, withdraw.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<PendingReviewMaintenance>();
            await maintenance.CleanupDuplicatePendingAsync(CancellationToken.None);
        }

        var pending = await PendingAsync(client);
        var item = Assert.Single(pending);
        Assert.Equal("Withdrawal", item.SubmissionKind);
        Assert.Equal("Withdrawal", item.RequestStatus);
        Assert.Equal(33, item.FlagGridId);
        Assert.Equal("V-33", item.FlagGridName);
        Assert.Equal(8, item.HonoreeId);
        Assert.Contains("Hulett", item.HonoreeName);
        Assert.Equal("Hulett Submitter", item.ClaimantName);
        Assert.NotNull(item.SubmittedUtc);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var honoree = await db.Honorees.SingleAsync(record => record.Id == 8);
            Assert.True(honoree.IsActive);
            Assert.Null(honoree.DeletedDate);
            Assert.Equal(33, honoree.FlagGridId);
            Assert.Equal(33, await db.FlagClaims.Where(claim => claim.Id == 11).Select(claim => claim.FlagGridId).SingleAsync());
            Assert.Equal(2, await db.FlagClaims.CountAsync(claim => claim.Id == 11 || claim.Id == 90));
        }

        var forbidden = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{item.ChangeRequestId}/removed-from-field",
            "someone-else",
            "else@example.com",
            "Someone Else"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var confirmed = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{item.ChangeRequestId}/removed-from-field",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Empty(await PendingAsync(client));

        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var removed = await saved.Honorees.SingleAsync(record => record.Id == 8);
        Assert.False(removed.IsActive);
        Assert.NotNull(removed.DeletedDate);
        Assert.Null(removed.FlagGridId);
        Assert.Null(await saved.FlagGrids.Where(grid => grid.Id == 33).Select(grid => grid.HonoreeId).SingleAsync());
        Assert.Null(await saved.FlagClaims.Where(claim => claim.Id == 11).Select(claim => claim.FlagGridId).SingleAsync());
        Assert.Equal("Removed", await saved.HonoreeChangeRequests.Where(request => request.RequestStatus == "Removed").Select(request => request.RequestStatus).SingleAsync());
    }

    [Fact]
    public async Task Reprint_queue_get_does_not_clear_other_active_rows()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await SeedReprintAsync(61, 71, now.AddMinutes(-2));
        await SeedReprintAsync(62, 72, now.AddMinutes(-1));
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/approved-reprint-queue",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<QueueItem>>(JsonOptions);
        Assert.Equal(2, items!.Count);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(2, await db.HonoreeChangeRequests.CountAsync(request => request.RequiresCardReprint));
    }

    [Fact]
    public async Task Card_preview_returns_a_pdf_for_an_admin_and_forbids_everyone_else()
    {
        await ResetAsync();
        await SeedSubmittedAsync(15, 4, flagGridId: null, DateTime.UtcNow, photo: null, honoreeId: null);
        var client = factory.CreateClient();

        var admin = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/15/card-preview",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));
        var member = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/15/card-preview",
            "member",
            "member@example.com",
            "Member"));

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal("application/pdf", admin.Content.Headers.ContentType?.MediaType);
        var pdf = await admin.Content.ReadAsByteArrayAsync();
        Assert.True(pdf.Length > 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
    }

    [Fact]
    public async Task Pending_change_includes_the_rank_diff()
    {
        await ResetAsync();
        await SeedHonoreeAsync(9, "Private");
        await SeedSubmittedAsync(
            30,
            50,
            flagGridId: 60,
            DateTime.UtcNow,
            photo: null,
            honoreeId: 9,
            rank: "Sergeant");

        var item = Assert.Single(await PendingAsync(factory.CreateClient()));

        Assert.Equal("Change", item.SubmissionKind);
        var rank = Assert.Single(item.FieldChanges!, change => change.Label == "Rank");
        Assert.Equal("Private", rank.Previous);
        Assert.Equal("Sergeant", rank.Proposed);
    }

    [Fact]
    public async Task Cleanup_releases_spots_held_by_submitted_rejected_and_draft_new_submissions()
    {
        await ResetAsync();
        await SeedGridAsync(88, "D-01");
        await SeedGridAsync(77, "D-02");
        await SeedGridAsync(66, "D-03");
        await SeedGridAsync(55, "D-04");
        await SeedHonoreeAsync(5, "Private");
        await SeedSubmittedAsync(81, 91, 88, DateTime.UtcNow, photo: null, honoreeId: null);
        await SeedDraftAsync(82, 92, 77, DateTime.UtcNow, honoreeId: null, claimStatus: "Claimed");
        await SeedRequestAsync(83, 93, 66, DateTime.UtcNow, photo: null, honoreeId: null, owner: "owner-photo", requestStatus: "Rejected", claimStatus: "Rejected", rank: null);
        await SeedRequestAsync(84, 94, 55, DateTime.UtcNow, photo: null, honoreeId: 5, owner: "owner-photo", requestStatus: "Draft", claimStatus: "Claimed", rank: null);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<PendingReviewMaintenance>();
            await maintenance.CleanupDuplicatePendingAsync(CancellationToken.None);
        }

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Null(await db.HonoreeChangeRequests.Where(request => request.Id == 81).Select(request => request.FlagGridId).SingleAsync());
        Assert.Null(await db.FlagClaims.Where(claim => claim.Id == 91).Select(claim => claim.FlagGridId).SingleAsync());
        Assert.Null(await db.HonoreeChangeRequests.Where(request => request.Id == 82).Select(request => request.FlagGridId).SingleAsync());
        Assert.Null(await db.FlagClaims.Where(claim => claim.Id == 92).Select(claim => claim.FlagGridId).SingleAsync());
        Assert.Null(await db.HonoreeChangeRequests.Where(request => request.Id == 83).Select(request => request.FlagGridId).SingleAsync());
        Assert.Null(await db.FlagClaims.Where(claim => claim.Id == 93).Select(claim => claim.FlagGridId).SingleAsync());
        Assert.Equal(55, await db.HonoreeChangeRequests.Where(request => request.Id == 84).Select(request => request.FlagGridId).SingleAsync());
        Assert.Equal(55, await db.FlagClaims.Where(claim => claim.Id == 94).Select(claim => claim.FlagGridId).SingleAsync());
    }

    [Fact]
    public async Task Two_submitted_claims_without_a_spot_can_coexist()
    {
        await ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var index = db.Model.FindEntityType(typeof(FlagClaim))!
            .GetIndexes()
            .Single(item => item.GetDatabaseName() == "UX_FlagClaims_ActiveFlagGrid");
        var filter = index.GetFilter();
        Assert.NotNull(filter);
        Assert.True(index.IsUnique);
        Assert.Contains("[FlagGridId] IS NOT NULL", filter, StringComparison.Ordinal);
        Assert.Contains("Claimed", filter, StringComparison.Ordinal);
        Assert.Contains("Submitted", filter, StringComparison.Ordinal);
        Assert.Contains("Approved", filter, StringComparison.Ordinal);
        Assert.Contains("UX_FlagClaims_ActiveFlagGrid", PendingSubmissionGridSchema.ActiveFlagGridIndexSql, StringComparison.Ordinal);
        Assert.Contains("[[]FlagGridId] IS NOT NULL", PendingSubmissionGridSchema.ActiveFlagGridIndexSql, StringComparison.Ordinal);
        Assert.Contains("[FlagGridId] IS NOT NULL", PendingSubmissionGridSchema.ActiveFlagGridIndexSql, StringComparison.Ordinal);

        var now = DateTime.UtcNow;
        db.FlagClaims.Add(new FlagClaim
        {
            FlagGridId = null,
            ClaimStatus = "Submitted",
            ExternalUserObjectId = "spotless-a",
            ExternalUserEmail = "a@example.com",
            CreatedUtc = now
        });
        db.FlagClaims.Add(new FlagClaim
        {
            FlagGridId = null,
            ClaimStatus = "Submitted",
            ExternalUserObjectId = "spotless-b",
            ExternalUserEmail = "b@example.com",
            CreatedUtc = now
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.FlagClaims.CountAsync(claim => claim.FlagGridId == null && claim.ClaimStatus == "Submitted"));
    }

    [Fact]
    public async Task Rejected_and_unclaimed_claims_do_not_block_the_next_spot()
    {
        await ResetAsync();
        await SeedGridAsync(1, "A-01");
        await SeedGridAsync(2, "C-28");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            db.FlagClaims.Add(new FlagClaim
            {
                FlagGridId = 1,
                ClaimStatus = "Rejected",
                ExternalUserObjectId = "rejected",
                ExternalUserEmail = "rejected@example.com",
                CreatedUtc = now
            });
            db.FlagClaims.Add(new FlagClaim
            {
                FlagGridId = 1,
                ClaimStatus = "Unclaimed",
                ExternalUserObjectId = "unclaimed",
                ExternalUserEmail = "unclaimed@example.com",
                CreatedUtc = now
            });
            db.FlagClaims.Add(new FlagClaim
            {
                FlagGridId = 2,
                ClaimStatus = "Claimed",
                ExternalUserObjectId = "claimed",
                ExternalUserEmail = "claimed@example.com",
                CreatedUtc = now
            });
            await db.SaveChangesAsync();
        }

        await using var check = factory.Services.CreateAsyncScope();
        var assignment = check.ServiceProvider.GetRequiredService<FlagGridAssignment>();
        var next = await assignment.NextAvailableAsync(CancellationToken.None);
        Assert.NotNull(next);
        Assert.Equal("A-01", next.FlagGridName);
    }

    private async Task ResetAsync()
    {
        factory.Files.Reset();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.Sponsors.RemoveRange(db.Sponsors);
        db.ServiceBranches.RemoveRange(db.ServiceBranches);
        db.ServiceBranchCategories.RemoveRange(db.ServiceBranchCategories);
        db.SponsorCategories.RemoveRange(db.SponsorCategories);
        await db.SaveChangesAsync();
    }

    private async Task SeedCatalogAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        db.SponsorCategories.Add(new SponsorCategory
        {
            Id = 1,
            SponsorCategoryName = "Family",
            Description = "Family",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.ServiceBranchCategories.Add(new ServiceBranchCategory
        {
            Id = 1,
            ServiceBranchCategoryName = "Armed forces",
            Description = "Armed forces",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.ServiceBranches.Add(new ServiceBranch
        {
            Id = 1,
            ServiceBranchCategoryId = 1,
            ServiceBranchName = "Army",
            Description = "Army",
            LogoFileName = string.Empty,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedGridAsync(int id, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        if (await db.FlagGrids.AnyAsync(grid => grid.Id == id))
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.FlagGrids.Add(new FlagGrid
        {
            Id = id,
            FlagGridName = name,
            Notes = string.Empty,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        await db.SaveChangesAsync();
    }

    private async Task<(int ChangeId, int ClaimId)> SeedNewSubmissionAsync(int? flagGridId)
    {
        var claimId = 70;
        var changeId = 80;
        await SeedSubmittedAsync(changeId, claimId, flagGridId, DateTime.UtcNow, photo: null, honoreeId: null, owner: "submitter");
        return (changeId, claimId);
    }

    private async Task SeedHonoreeAsync(int id, string rank)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        db.Honorees.Add(new Honoree
        {
            Id = id,
            FirstName = "Ada",
            LastName = "Lovelace",
            Rank = rank,
            Description = "A tribute",
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
        });
        await db.SaveChangesAsync();
    }

    private Task SeedSubmittedAsync(
        int changeId,
        int claimId,
        int? flagGridId,
        DateTime submittedUtc,
        string? photo,
        int? honoreeId,
        string owner = "owner-photo",
        string? rank = null) =>
        SeedRequestAsync(changeId, claimId, flagGridId, submittedUtc, photo, honoreeId, owner, "Submitted", "Submitted", rank);

    private Task SeedDraftAsync(
        int changeId,
        int claimId,
        int? flagGridId,
        DateTime createdUtc,
        int? honoreeId,
        string owner = "submitter",
        string claimStatus = "Submitted") =>
        SeedRequestAsync(changeId, claimId, flagGridId, createdUtc, photo: null, honoreeId, owner, "Draft", claimStatus, rank: null);

    private async Task SeedRequestAsync(
        int changeId,
        int claimId,
        int? flagGridId,
        DateTime timestamp,
        string? photo,
        int? honoreeId,
        string owner,
        string requestStatus,
        string claimStatus,
        string? rank)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        if (flagGridId is int gridId && !await db.FlagGrids.AnyAsync(grid => grid.Id == gridId))
        {
            db.FlagGrids.Add(new FlagGrid
            {
                Id = gridId,
                FlagGridName = $"G-{gridId}",
                Notes = string.Empty,
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = timestamp,
                ModifiedDate = timestamp
            });
        }

        if (!await db.FlagClaims.AnyAsync(claim => claim.Id == claimId))
        {
            db.FlagClaims.Add(new FlagClaim
            {
                Id = claimId,
                FlagGridId = flagGridId,
                HonoreeId = honoreeId,
                ExternalUserObjectId = owner,
                ExternalUserEmail = owner == "submitter" ? "submitter@example.com" : "owner@example.com",
                ExternalUserName = "Olivia Owner",
                ClaimStatus = claimStatus,
                CreatedUtc = timestamp,
                SubmittedUtc = requestStatus == "Submitted" ? timestamp : null
            });
        }

        db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            Id = changeId,
            FlagClaimId = claimId,
            FlagGridId = flagGridId,
            HonoreeId = honoreeId,
            FirstName = "Ada",
            LastName = "Lovelace",
            Rank = rank,
            Description = "A tribute",
            PhotoFileName = photo,
            RequestStatus = requestStatus,
            CreatedUtc = timestamp,
            SubmittedUtc = requestStatus == "Submitted" ? timestamp : null
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Remove_from_reprint_queue_succeeds_for_an_internal_claim_on_a_shared_grid()
    {
        await ResetAsync();
        const string creationNote = "Internal claim created to add an existing honoree to the card reprint queue.";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 10,
                FlagGridId = 3,
                HonoreeId = 5,
                ExternalUserObjectId = "owner",
                ExternalUserEmail = "owner@example.com",
                ClaimStatus = "Approved",
                CreatedUtc = DateTime.UtcNow
            });
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 11,
                FlagGridId = 3,
                HonoreeId = 5,
                ExternalUserObjectId = "admin-reprint:abc",
                ExternalUserEmail = "admin@example.com",
                ClaimStatus = "AdminReprintQueued",
                AdminNotes = creationNote,
                CreatedUtc = DateTime.UtcNow
            });
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                Id = 21,
                FlagClaimId = 11,
                FlagGridId = 3,
                HonoreeId = 5,
                FirstName = "Ada",
                LastName = "Lovelace",
                RequestStatus = "Approved",
                RequiresCardReprint = true,
                ReviewNotes = "Added to the reprint queue by administrator.",
                CreatedUtc = DateTime.UtcNow
            });
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 12,
                FlagGridId = 4,
                HonoreeId = 6,
                ExternalUserObjectId = "owner-2",
                ExternalUserEmail = "owner2@example.com",
                ClaimStatus = "Approved",
                CreatedUtc = DateTime.UtcNow
            });
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                Id = 22,
                FlagClaimId = 12,
                FlagGridId = 4,
                HonoreeId = 6,
                FirstName = "Grace",
                LastName = "Hopper",
                RequestStatus = "Approved",
                RequiresCardReprint = true,
                CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var missing = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/review/reprint-queue/remove",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin",
            body: new { changeRequestIds = (int[]?)null }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/review/reprint-queue/remove",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin",
            body: new { changeRequestIds = new[] { 21, 22 } }));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());

        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var removed = await saved.HonoreeChangeRequests.Include(r => r.FlagClaim).SingleAsync(r => r.Id == 21);
        Assert.False(removed.RequiresCardReprint);
        Assert.Equal("admin@example.com", removed.ReviewedBy);
        Assert.NotNull(removed.ReviewedUtc);
        Assert.Contains("Removed from the reprint queue by administrator.", removed.ReviewNotes, StringComparison.Ordinal);
        Assert.Equal("Cancelled", removed.FlagClaim!.ClaimStatus);
        Assert.Equal(creationNote, removed.FlagClaim.AdminNotes);
        var owner = await saved.FlagClaims.SingleAsync(c => c.Id == 10);
        Assert.Equal("Approved", owner.ClaimStatus);

        var approvedReprint = await saved.HonoreeChangeRequests.Include(r => r.FlagClaim).SingleAsync(r => r.Id == 22);
        Assert.False(approvedReprint.RequiresCardReprint);
        Assert.Equal("Approved", approvedReprint.FlagClaim!.ClaimStatus);
    }

    private async Task SeedReprintAsync(int changeId, int claimId, DateTime reviewedUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.FlagClaims.Add(new FlagClaim
        {
            Id = claimId,
            HonoreeId = 5,
            ExternalUserObjectId = $"reprint-{claimId}",
            ExternalUserEmail = "admin@example.com",
            ExternalUserName = "Admin",
            ClaimStatus = "Approved",
            CreatedUtc = reviewedUtc
        });
        db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            Id = changeId,
            FlagClaimId = claimId,
            HonoreeId = 5,
            FirstName = "Ada",
            LastName = "Lovelace",
            Description = "A tribute",
            RequestStatus = "Approved",
            RequiresCardReprint = true,
            CreatedUtc = reviewedUtc,
            SubmittedUtc = reviewedUtc,
            ReviewedUtc = reviewedUtc
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<PendingItem>> PendingAsync(HttpClient client)
    {
        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/pending",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<List<PendingItem>>(JsonOptions) ?? [];
    }

    private static MultipartFormDataContent Nomination(byte[]? photo)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("Test"), "FirstName" },
            { new StringContent("Submitter"), "LastName" },
            { new StringContent("1"), "ServiceBranchId" },
            { new StringContent("1"), "ServiceBranchCategoryId" },
            { new StringContent("A tribute for the honoree"), "Description" },
            { new StringContent("2145550100"), "SubmitterPhoneNumber" },
            { new StringContent("submitter@example.com"), "SubmitterEmailAddress" },
            { new StringContent("false"), "Kia" }
        };

        if (photo is not null)
        {
            var file = new ByteArrayContent(photo);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(file, "Photo", "10_result_trophy.png");
        }

        return content;
    }

    private static HttpRequestMessage Authed(
        HttpMethod method,
        string url,
        string ownerObjectId,
        string email,
        string name,
        string? roles = null,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, ownerObjectId);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        if (body is HttpContent content)
        {
            request.Content = content;
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private sealed record ClaimResponse(int Id, int? FlagGridId, string? HonoreeImageUrl, ChangeResponse? LatestChangeRequest);

    private sealed record ChangeResponse(int Id, string? PhotoFileName, int? FlagGridId, int? HonoreeId);

    private sealed record ApproveResponse(
        int? FlagGridId,
        string? FlagGridName,
        bool? FlagSpotAssigned,
        string? FlagSpotNotice,
        string? SubmissionKind);

    private sealed record PendingItem(
        int ChangeRequestId,
        int ClaimId,
        int? FlagGridId,
        string? FlagGridName,
        int? HonoreeId,
        string? PhotoFileName,
        string? SubmissionKind,
        string? Rank,
        List<FieldChange>? FieldChanges,
        string? HonoreeName = null,
        string? RequestStatus = null,
        string? ClaimantName = null,
        DateTime? SubmittedUtc = null);

    private sealed record FieldChange(string Label, string Previous, string Proposed);

    private sealed record QueueItem(int ChangeRequestId, int? HonoreeId);
}
