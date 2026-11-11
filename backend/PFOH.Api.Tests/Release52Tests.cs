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

public class Release52Tests : IDisposable
{
    private readonly PfohApiFactory factory = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task Remove_from_grid_requires_a_reason_and_records_the_full_outcome()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        await SeedClaimAsync(40, 1, 11, "Claimed", "Submitted");
        await SeedReprintAsync(50, 41, 1, 11);
        var client = factory.CreateClient();

        var missing = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear", new { reason = "  " }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("reason is required", await missing.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var removed = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear", new
        {
            reason = "The family asked to take this flag down",
            acknowledgeDuplicates = true
        }));
        var body = await removed.Content.ReadAsStringAsync();
        Assert.True(removed.StatusCode == HttpStatusCode.OK, body);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync(item => item.Id == 1);
        Assert.False(honoree.IsActive);
        Assert.NotNull(honoree.DeletedDate);
        Assert.Equal("admin@example.com", honoree.DeletedBy);
        Assert.Null(honoree.FlagGridId);
        Assert.Null(await db.FlagGrids.Where(grid => grid.Id == 11).Select(grid => grid.HonoreeId).SingleAsync());

        var queue = Assert.Single(await db.FlagRemovalQueueItems.ToListAsync());
        Assert.Equal(FlagRemovalQueue.Manual, queue.Source);
        Assert.Equal("admin@example.com", queue.CreatedBy);
        Assert.Contains("family asked", queue.SourceDetail);
        Assert.Null(queue.CanceledUtc);

        var claim = await db.FlagClaims.SingleAsync(item => item.Id == 40);
        Assert.Equal("Unclaimed", claim.ClaimStatus);
        var pending = await db.HonoreeChangeRequests.SingleAsync(item => item.Id == 70);
        Assert.Equal("Cancelled", pending.RequestStatus);
        var reprint = await db.HonoreeChangeRequests.SingleAsync(item => item.Id == 50);
        Assert.False(reprint.RequiresCardReprint);

        var audit = Assert.Single(await db.FlagGridRemovalAudits.ToListAsync());
        Assert.Equal("Remove", audit.Action);
        Assert.Equal("admin@example.com", audit.ActorEmail);
        Assert.Equal("admin-1", audit.ActorObjectId);
        Assert.Equal("The family asked to take this flag down", audit.Reason);
        Assert.Contains("\"isActive\":true", audit.BeforeState, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"isActive\":false", audit.AfterState, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("40", audit.BeforeState);
        Assert.Contains("Unclaimed", audit.AfterState);

        var reprintEvent = Assert.Single(await db.ReprintQueueEvents.ToListAsync());
        Assert.Equal(ReprintQueueActions.Remove, reprintEvent.Action);
        Assert.Equal("flag-removal", reprintEvent.Source);
        Assert.Equal("admin@example.com", reprintEvent.ActorEmail);
        Assert.Equal(50, reprintEvent.ChangeRequestId);
    }

    [Fact]
    public async Task Remove_warns_about_a_similar_honoree_on_another_grid_until_confirmed()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Mann", 11, "A-01", gridHonoreeId: 1);
        await SeedOnGridAsync(2, "Ada", "Mann", 12, "B-04", gridHonoreeId: 2);
        var client = factory.CreateClient();

        var blocked = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/honorees/1/remove-from-grid", new
        {
            reason = "Checking the duplicate warning"
        }));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using var json = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal(FlagGridRemovalService.DuplicateCode, json.RootElement.GetProperty("code").GetString());
        var duplicates = json.RootElement.GetProperty("duplicates").EnumerateArray().ToList();
        Assert.Contains(duplicates, item => item.GetProperty("honoreeId").GetInt32() == 2);
        Assert.Equal("B-04", duplicates.Single(item => item.GetProperty("honoreeId").GetInt32() == 2).GetProperty("flagGridName").GetString());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            Assert.True(await db.Honorees.Where(item => item.Id == 1).Select(item => item.IsActive).SingleAsync());
        }

        var confirmed = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/honorees/1/remove-from-grid", new
        {
            reason = "Not a duplicate",
            acknowledgeDuplicates = true
        }));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
    }

    [Fact]
    public async Task Remove_frees_a_grid_whose_honoree_id_is_null()
    {
        await ResetAsync();
        await SeedOnGridAsync(8, "Grace", "Hopper", 18, "C-03", gridHonoreeId: null);
        var client = factory.CreateClient();
        var removed = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/18/clear", new
        {
            reason = "The grid row did not point back",
            acknowledgeDuplicates = true
        }));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync(item => item.Id == 8);
        Assert.False(honoree.IsActive);
        Assert.Null(honoree.FlagGridId);
        Assert.Null(await db.FlagGrids.Where(grid => grid.Id == 18).Select(grid => grid.HonoreeId).SingleAsync());
        Assert.Single(await db.FlagRemovalQueueItems.ToListAsync());
    }

    [Fact]
    public async Task Restore_puts_the_honoree_back_and_is_blocked_when_the_spot_is_taken()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        await SeedOnGridAsync(2, "Grace", "Hopper", 12, "B-02", gridHonoreeId: 2);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear", new
        {
            reason = "Temporary removal",
            acknowledgeDuplicates = true
        }))).StatusCode);

        int queueId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            queueId = await db.FlagRemovalQueueItems.Select(item => item.Id).SingleAsync();
            var other = await db.Honorees.SingleAsync(item => item.Id == 2);
            other.FlagGridId = 11;
            (await db.FlagGrids.SingleAsync(item => item.Id == 11)).HonoreeId = 2;
            (await db.FlagGrids.SingleAsync(item => item.Id == 12)).HonoreeId = null;
            await db.SaveChangesAsync();
        }

        var blocked = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/removal-queue/{queueId}/restore"));
        var blockedBody = await blocked.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("now assigned to", blockedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restore is blocked", blockedBody, StringComparison.OrdinalIgnoreCase);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var other = await db.Honorees.SingleAsync(item => item.Id == 2);
            other.FlagGridId = 12;
            (await db.FlagGrids.SingleAsync(item => item.Id == 11)).HonoreeId = null;
            (await db.FlagGrids.SingleAsync(item => item.Id == 12)).HonoreeId = 2;
            await db.SaveChangesAsync();
        }

        var restored = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/removal-queue/{queueId}/restore"));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await saved.Honorees.SingleAsync(item => item.Id == 1);
        Assert.True(honoree.IsActive);
        Assert.Null(honoree.DeletedDate);
        Assert.Equal(11, honoree.FlagGridId);
        Assert.Equal(1, await saved.FlagGrids.Where(grid => grid.Id == 11).Select(grid => grid.HonoreeId).SingleAsync());
        var row = await saved.FlagRemovalQueueItems.SingleAsync();
        Assert.NotNull(row.CanceledUtc);
        Assert.Equal(FlagGridRemovalService.RestoreCancelReason, row.CancelReason);
        Assert.Equal("admin@example.com", row.CanceledBy);
        Assert.Contains(await saved.FlagGridRemovalAudits.Select(item => item.Action).ToListAsync(), action => action == "Restore");
    }

    [Fact]
    public async Task Failed_pdf_rebuild_does_not_undo_the_removal()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        factory.Files.FailPdfSave = true;
        var client = factory.CreateClient();
        var removed = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear", new
        {
            reason = "Save even if the PDF fails",
            acknowledgeDuplicates = true
        }));
        var body = await removed.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Contains("could not be rebuilt", body.GetProperty("pdfWarning").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.False(await db.Honorees.Where(item => item.Id == 1).Select(item => item.IsActive).SingleAsync());
        Assert.Null(await db.FlagGrids.Where(grid => grid.Id == 11).Select(grid => grid.HonoreeId).SingleAsync());
    }

    [Fact]
    public async Task Pdf_open_rebuilds_a_stale_file_and_hides_an_inactive_honoree()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        factory.Files.Pdfs[1] = "%PDF-old"u8.ToArray();
        factory.Files.PdfStamps[1] = "stale";
        var client = factory.CreateClient();

        var opened = await client.GetAsync("/api/honorees/1/pdf");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal("no-store", opened.Headers.CacheControl?.ToString());
        Assert.False(string.IsNullOrWhiteSpace(opened.Headers.ETag?.Tag));
        Assert.NotEqual("%PDF-old", System.Text.Encoding.UTF8.GetString(await opened.Content.ReadAsByteArrayAsync()));
        Assert.Equal(await CurrentStampAsync(1), factory.Files.PdfStamps[1]);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var honoree = await db.Honorees.SingleAsync(item => item.Id == 1);
            honoree.IsActive = false;
            honoree.DeletedDate = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var hidden = await client.GetAsync("/api/honorees/1/pdf");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("no-store", hidden.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_v1_card_stamp_is_rebuilt_on_open_and_startup_does_not_render()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Alex", "Rivera", 11, "AA-10", gridHonoreeId: 1);
        var client = factory.CreateClient();
        Assert.Equal(0, factory.Renderer.Calls);

        var v1 = await CurrentStampAsync(1, formatVersion: 1);
        var current = await CurrentStampAsync(1);
        Assert.Equal(2, HonoreePdfStamp.FormatVersion);
        Assert.NotEqual(v1, current);
        factory.Files.Pdfs[1] = "%PDF-v1"u8.ToArray();
        factory.Files.PdfStamps[1] = v1;

        var opened = await client.GetAsync("/api/honorees/1/pdf");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.NotEqual("%PDF-v1", System.Text.Encoding.UTF8.GetString(await opened.Content.ReadAsByteArrayAsync()));
        Assert.Equal(1, factory.Renderer.Calls);
        Assert.Equal(current, factory.Files.PdfStamps[1]);

        var again = await client.GetAsync("/api/honorees/1/pdf");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, factory.Renderer.Calls);
    }

    [Fact]
    public async Task Regenerate_pdf_is_admin_only_and_hides_storage_internals()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        var client = factory.CreateClient();

        var anonymous = await client.PostAsync("/api/honorees/1/pdf/regenerate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var member = await client.SendAsync(Admin(HttpMethod.Post, "/api/honorees/1/pdf/regenerate", roles: "", oid: "member-1"));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);

        var admin = await client.SendAsync(Admin(HttpMethod.Post, "/api/honorees/1/pdf/regenerate"));
        var body = await admin.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.DoesNotContain("storageAccount", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("container", body, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("uploaded").GetBoolean());
        Assert.Equal("PDF regenerated.", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_reason_of_only_invisible_characters_is_rejected()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        var client = factory.CreateClient();
        var blank = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear", new
        {
            reason = "\u200B\u200C\u200D\uFEFF\u2060"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var kept = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear", new
        {
            reason = "\u200BFamily request\uFEFF",
            acknowledgeDuplicates = true
        }));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var audit = Assert.Single(await db.FlagGridRemovalAudits.ToListAsync());
        Assert.Equal("Family request", audit.Reason);
    }

    [Fact]
    public async Task A_second_open_waits_for_the_honoree_pdf_lock()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        factory.Files.SaveDelay = TimeSpan.FromMilliseconds(250);
        var client = factory.CreateClient();
        var first = client.GetAsync("/api/honorees/1/pdf");
        var second = client.GetAsync("/api/honorees/1/pdf");
        await Task.WhenAll(first, second);
        Assert.Equal(HttpStatusCode.OK, first.Result.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.Result.StatusCode);
        Assert.Equal(1, factory.Files.MaxConcurrentSaves);
    }

    [Fact]
    public async Task Mutation_paths_refresh_the_pdf_stamp()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        var client = factory.CreateClient();

        await SeedSubmittedAsync(80, 90, 1, 11, "Ada", "Byron");
        var approved = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/80/approve", new
        {
            requiresCardReprint = false,
            reviewNotes = "Approved without a reprint"
        }));
        var approvedBody = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.OK, approvedBody);
        Assert.Equal(await CurrentStampAsync(1), factory.Files.PdfStamps[1]);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            Assert.DoesNotContain(await db.ReprintQueueEvents.Select(item => item.Action).ToListAsync(), action => action == ReprintQueueActions.Add);
        }

        factory.Files.PdfStamps.Remove(1);
        await SeedSubmittedAsync(81, 91, 1, 11, "Ada", "King");
        var reprinted = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/81/approve", new
        {
            requiresCardReprint = true,
            reviewNotes = "Approved with a reprint"
        }));
        Assert.True(reprinted.StatusCode == HttpStatusCode.OK, await reprinted.Content.ReadAsStringAsync());
        Assert.Equal(await CurrentStampAsync(1), factory.Files.PdfStamps[1]);

        factory.Files.PhotoEtags["pending/3.jpg"] = "etag-old";
        factory.Files.Photos["pending/3.jpg"] = [1, 2, 3, 4];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 100,
                HonoreeId = 1,
                FlagGridId = 11,
                ExternalUserObjectId = "admin-edit",
                ExternalUserEmail = "admin@example.com",
                ClaimStatus = "AdminDirectEdit",
                CreatedUtc = DateTime.UtcNow
            });
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                Id = 3,
                FlagClaimId = 100,
                FlagGridId = 11,
                HonoreeId = 1,
                FirstName = "Ada",
                LastName = "Lovelace",
                Description = "Updated tribute",
                PhotoFileName = "pending/3.jpg",
                RequestStatus = "Draft",
                CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var applied = await client.SendAsync(Admin(HttpMethod.Post, "/api/flag-claims/100/admin-apply-reprint"));
        Assert.True(applied.StatusCode == HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());
        Assert.Equal(await CurrentStampAsync(1), factory.Files.PdfStamps[1]);

        await SeedOnGridAsync(4, "Ada", "Lovelace", 14, "D-02", gridHonoreeId: 4, description: "Second tribute");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var branch = new ServiceBranch
            {
                Id = 1,
                ServiceBranchName = "Army",
                Description = "Army",
                LogoFileName = "Army.png",
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow
            };
            db.ServiceBranches.Add(branch);
            foreach (var id in new[] { 1, 4 })
            {
                var person = await db.Honorees.SingleAsync(item => item.Id == id);
                person.ServiceBranchId = 1;
                person.Description = id == 1 ? "Updated tribute" : "Second tribute";
            }
            await db.SaveChangesAsync();
        }

        var fields = MergeFields(1);
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 1,
            duplicateIds = new[] { 1, 4 },
            fields
        }));
        Assert.True(merged.StatusCode == HttpStatusCode.OK, await merged.Content.ReadAsStringAsync());
        Assert.Equal(await CurrentStampAsync(1), factory.Files.PdfStamps[1]);

        int mergeId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            mergeId = await db.HonoreeMergeAudits.Select(item => item.Id).SingleAsync();
        }

        var undo = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{mergeId}/undo"));
        Assert.True(undo.StatusCode == HttpStatusCode.OK, await undo.Content.ReadAsStringAsync());
        Assert.Equal(await CurrentStampAsync(1), factory.Files.PdfStamps[1]);
        Assert.True(factory.Files.PdfStamps.ContainsKey(4));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                Id = 120,
                FlagClaimId = 90,
                FlagGridId = 11,
                HonoreeId = 1,
                FirstName = "Ada",
                LastName = "Lovelace",
                Description = "Please remove",
                RequestStatus = PendingReviewMaintenance.Withdrawal,
                CreatedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var withdrawal = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/120/removed-from-field"));
        Assert.True(withdrawal.StatusCode == HttpStatusCode.OK, await withdrawal.Content.ReadAsStringAsync());
        var hidden = await client.GetAsync("/api/honorees/1/pdf");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("no-store", hidden.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Reprint_removals_record_the_actor_for_one_row_a_bulk_remove_and_a_dedup()
    {
        await ResetAsync();
        await SeedOnGridAsync(1, "Ada", "Lovelace", 11, "A-12", gridHonoreeId: 1);
        await SeedReprintAsync(21, 31, 1, 11);
        await SeedReprintAsync(22, 32, 1, 11);
        var client = factory.CreateClient();

        var missing = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/reprint-queue/remove", new
        {
            changeRequestIds = new[] { 21 }
        }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var one = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/reprint-queue/remove", new
        {
            changeRequestIds = new[] { 21 },
            reason = "Already handed to the family",
            source = "row"
        }));
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);

        await SeedReprintAsync(23, 33, 1, 11);
        var bulk = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/reprint-queue/remove", new
        {
            changeRequestIds = new[] { 22, 23 },
            reason = "Clearing the rest of the batch",
            source = "bulk"
        }));
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);

        await SeedReprintAsync(24, 34, 1, 11);
        await SeedReprintAsync(25, 35, 1, 11);
        var queued = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/honoree/1/queue-reprint"));
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);

        var printed = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/print/mark-printed", new
        {
            changeRequestIds = new[] { 25 },
            comment = "Handed out at the field"
        }));
        Assert.Equal(HttpStatusCode.OK, printed.StatusCode);
        var added = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/honoree/1/queue-reprint"));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var events = await db.ReprintQueueEvents.OrderBy(item => item.Id).ToListAsync();
        var single = Assert.Single(events, item => item.ChangeRequestId == 21 && item.Action == ReprintQueueActions.Remove);
        Assert.Equal("admin@example.com", single.ActorEmail);
        Assert.Equal("admin-1", single.ActorObjectId);
        Assert.Equal("Already handed to the family", single.Reason);
        Assert.Equal("row", single.Source);
        Assert.Equal(1, single.BatchSize);
        Assert.True(single.Utc > DateTime.UtcNow.AddMinutes(-5));

        var bulkEvents = events.Where(item => item.Action == ReprintQueueActions.Remove && item.Source == "bulk").ToList();
        Assert.Equal(2, bulkEvents.Count);
        Assert.All(bulkEvents, item => Assert.Equal(2, item.BatchSize));
        Assert.All(bulkEvents, item => Assert.Equal("Clearing the rest of the batch", item.Reason));

        Assert.Contains(events, item => item.Action == ReprintQueueActions.DedupRemove && item.Source == "dedup");
        Assert.Contains(events, item => item.Action == ReprintQueueActions.Add && item.Source == "queue");
        var printedEvent = Assert.Single(events, item => item.Action == ReprintQueueActions.Printed);
        Assert.Equal("admin@example.com", printedEvent.ActorEmail);
        Assert.Equal("Handed out at the field", printedEvent.Reason);
        Assert.Equal("print", printedEvent.Source);

        var history = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/print/history"));
        var historyText = await history.Content.ReadAsStringAsync();
        using var historyJson = JsonDocument.Parse(historyText);
        var removedRows = historyJson.RootElement.GetProperty("removed").EnumerateArray().ToList();
        Assert.Contains(removedRows, item => item.GetProperty("reason").GetString() == "Already handed to the family");
        Assert.All(removedRows, item => Assert.EndsWith("Z", item.GetProperty("utc").GetString()));
        Assert.Contains(historyJson.RootElement.GetProperty("printed").EnumerateArray(), item =>
            item.GetProperty("printedUtc").GetString()!.EndsWith("Z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admin_role_can_unclaim_a_flag_the_PFOH_Admin_check_used_to_reject()
    {
        await ResetAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 9,
                ExternalUserObjectId = "owner-1",
                ExternalUserEmail = "owner@example.com",
                ClaimStatus = "Claimed",
                CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/flag-claims/9/unclaim", roles: "Admin", oid: "admin-2"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("Unclaimed", await saved.FlagClaims.Where(item => item.Id == 9).Select(item => item.ClaimStatus).SingleAsync());
    }

    private async Task<string> CurrentStampAsync(int honoreeId, int? formatVersion = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<HonoreeFileStorage>();
        var honoree = await db.Honorees.Include(item => item.FlagGrid).Include(item => item.ServiceBranch).Include(item => item.Sponsor)
            .SingleAsync(item => item.Id == honoreeId);
        var etag = await files.GetPhotoEtagAsync(honoree.PhotoFileName, CancellationToken.None);
        var sponsor = honoree.Sponsor is null
            ? null
            : string.Join(" ", new[] { honoree.Sponsor.FirstName, honoree.Sponsor.LastName }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return formatVersion is int version
            ? HonoreePdfStamp.Compute(honoree, honoree.FlagGrid?.FlagGridName, honoree.ServiceBranch?.ServiceBranchName, sponsor, etag, version)
            : HonoreePdfStamp.Compute(honoree, honoree.FlagGrid?.FlagGridName, honoree.ServiceBranch?.ServiceBranchName, sponsor, etag);
    }

    private async Task ResetAsync()
    {
        factory.Files.Reset();
        factory.Renderer.Reset();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }

    private async Task SeedOnGridAsync(int id, string first, string last, int gridId, string gridName, int? gridHonoreeId, string description = "A tribute")
    {
        var now = DateTime.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        if (!await db.FlagGrids.AnyAsync(item => item.Id == gridId))
        {
            db.FlagGrids.Add(new FlagGrid
            {
                Id = gridId,
                FlagGridName = gridName,
                Notes = string.Empty,
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now,
                HonoreeId = gridHonoreeId
            });
        }

        if (!await db.Honorees.AnyAsync(item => item.Id == id))
        {
            db.Honorees.Add(new Honoree
            {
                Id = id,
                FirstName = first,
                LastName = last,
                Rank = "Private",
                Description = description,
                PhotoFileName = string.Empty,
                FlagGridId = gridId,
                Salutation = string.Empty,
                NameUserEntry = string.Empty,
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
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedClaimAsync(int claimId, int honoreeId, int gridId, string claimStatus, string requestStatus)
    {
        var now = DateTime.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.FlagClaims.Add(new FlagClaim
        {
            Id = claimId,
            HonoreeId = honoreeId,
            FlagGridId = gridId,
            ExternalUserObjectId = "owner-1",
            ExternalUserEmail = "owner@example.com",
            ClaimStatus = claimStatus,
            CreatedUtc = now
        });
        db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            Id = claimId + 30,
            FlagClaimId = claimId,
            HonoreeId = honoreeId,
            FlagGridId = gridId,
            FirstName = "Ada",
            LastName = "Lovelace",
            Description = "Pending edit",
            RequestStatus = requestStatus,
            CreatedUtc = now,
            SubmittedUtc = now
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedReprintAsync(int changeId, int claimId, int honoreeId, int gridId)
    {
        var now = DateTime.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.FlagClaims.Add(new FlagClaim
        {
            Id = claimId,
            HonoreeId = honoreeId,
            FlagGridId = gridId,
            ExternalUserObjectId = $"admin-reprint:{claimId}",
            ExternalUserEmail = "admin@example.com",
            ClaimStatus = "AdminReprintQueued",
            CreatedUtc = now
        });
        db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            Id = changeId,
            FlagClaimId = claimId,
            HonoreeId = honoreeId,
            FlagGridId = gridId,
            FirstName = "Ada",
            LastName = "Lovelace",
            Description = "Reprint",
            RequestStatus = "Approved",
            RequiresCardReprint = true,
            CreatedUtc = now,
            ReviewedUtc = now,
            ReviewedBy = "seed"
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSubmittedAsync(int changeId, int claimId, int honoreeId, int gridId, string first, string last)
    {
        var now = DateTime.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.FlagClaims.Add(new FlagClaim
        {
            Id = claimId,
            HonoreeId = honoreeId,
            FlagGridId = gridId,
            ExternalUserObjectId = "owner-1",
            ExternalUserEmail = "owner@example.com",
            ClaimStatus = "Submitted",
            CreatedUtc = now
        });
        db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            Id = changeId,
            FlagClaimId = claimId,
            HonoreeId = honoreeId,
            FlagGridId = gridId,
            FirstName = first,
            LastName = last,
            Description = "Submitted tribute",
            RequestStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        });
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, int> MergeFields(int survivor) => new()
    {
        ["firstName"] = survivor,
        ["middleName"] = survivor,
        ["lastName"] = survivor,
        ["suffix"] = survivor,
        ["nickname"] = survivor,
        ["rank"] = survivor,
        ["serviceBranch"] = survivor,
        ["serviceYears"] = survivor,
        ["conflictsServed"] = survivor,
        ["awards"] = survivor,
        ["description"] = survivor,
        ["kia"] = survivor,
        ["photo"] = survivor,
        ["flagGrid"] = survivor,
        ["phone"] = survivor,
        ["email"] = survivor,
        ["sponsor"] = survivor
    };

    private static HttpRequestMessage Admin(HttpMethod method, string url, object? body = null, string roles = "PFOH.Admin", string oid = "admin-1")
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, oid);
        request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Example Admin");
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
