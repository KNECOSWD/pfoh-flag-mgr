using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class FlagRemovalQueueTests : IDisposable
{
    private readonly PfohApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task Manual_clear_queues_one_open_item_and_a_later_move_relabels_it()
    {
        await ResetAsync();
        await SeedHonoreeAsync(1, "Example", "Honoree A", 11, "A-12");
        await SeedHonoreeAsync(2, "Example", "Honoree B", 12, "C-07");
        await SeedGridAsync(13, "D-03");
        var client = factory.CreateClient();

        var cleared = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear"));
        await AssertSavedAsync(cleared);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var item = Assert.Single(await db.FlagRemovalQueueItems.ToListAsync());
            Assert.Equal(FlagRemovalQueue.Manual, item.Source);
            Assert.Equal("A-12", item.FlagGridName);
            Assert.Equal("Example Honoree A", item.HonoreeName);
            Assert.Equal("admin@example.com", item.CreatedBy);
            Assert.Null(item.DoneUtc);
            Assert.Null(item.NewFlagGridId);
            Assert.Null(await db.FlagGrids.Where(grid => grid.Id == 11).Select(grid => grid.HonoreeId).SingleAsync());
        }

        var clearMove = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/12/clear"));
        await AssertSavedAsync(clearMove);
        var assignMove = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/13/assign", new { honoreeId = 2 }));
        await AssertSavedAsync(assignMove);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var rows = await db.FlagRemovalQueueItems.ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(FlagRemovalQueue.Manual, rows.Single(item => item.HonoreeId == 1).Source);
            var move = rows.Single(item => item.HonoreeId == 2);
            Assert.Equal(FlagRemovalQueue.Move, move.Source);
            Assert.Equal("C-07", move.FlagGridName);
            Assert.Equal(13, move.NewFlagGridId);
            Assert.Equal("D-03", move.NewFlagGridName);
            Assert.Null(move.SourceDetail);
            Assert.Equal(2, await db.FlagGrids.Where(grid => grid.Id == 13).Select(grid => grid.HonoreeId).SingleAsync());
        }
    }

    [Fact]
    public async Task Assigning_the_same_spot_cancels_the_open_item_and_a_first_assign_queues_nothing()
    {
        await ResetAsync();
        await SeedHonoreeAsync(1, "Example", "Honoree A", 11, "A-12");
        await SeedHonoreeAsync(3, "Example", "Honoree C", null, null);
        await SeedGridAsync(15, "B-01");
        var client = factory.CreateClient();

        var first = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/15/assign", new { honoreeId = 3 }));
        await AssertSavedAsync(first);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            Assert.Empty(await db.FlagRemovalQueueItems.ToListAsync());
        }

        var cleared = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear"));
        await AssertSavedAsync(cleared);
        var same = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/assign", new { honoreeId = 1 }));
        await AssertSavedAsync(same);

        var count = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/removal-queue/count"));
        Assert.Equal(HttpStatusCode.OK, count.StatusCode);
        var payload = await count.Content.ReadFromJsonAsync<CountPayload>();
        Assert.Equal(0, payload!.Count);

        var done = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/removal-queue?status=done"));
        var rows = await done.Content.ReadFromJsonAsync<List<QueueRow>>();
        var canceled = Assert.Single(rows!);
        Assert.Equal(FlagRemovalQueue.SameSpotReason, canceled.CancelReason);
        Assert.Equal("A-12", canceled.FlagGridName);
    }

    [Fact]
    public async Task Merge_queues_the_released_spot_and_undo_cancels_or_asks_to_put_it_back()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagGrids.Add(Grid(21, "B-04", now));
            db.FlagGrids.Add(Grid(22, "B-09", now));
            db.Honorees.Add(Person(4, "Example", "Honoree C", 21, now));
            db.Honorees.Add(Person(5, "Example", "Honoree D", 22, now));
            await db.SaveChangesAsync();
            db.FlagGrids.Single(grid => grid.Id == 21).HonoreeId = 4;
            db.FlagGrids.Single(grid => grid.Id == 22).HonoreeId = 5;
            await db.SaveChangesAsync();

            var confirmed = await db.Honorees.Where(honoree => honoree.Id == 4 || honoree.Id == 5).ToListAsync();
            var survivor = confirmed.Single(honoree => honoree.Id == 5);
            await HonoreeMerge.ApplyAsync(db, survivor, confirmed, Fields(5), null, "admin@example.com", Guid.NewGuid(), CancellationToken.None);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var item = Assert.Single(await db.FlagRemovalQueueItems.ToListAsync());
            Assert.Equal(FlagRemovalQueue.Merge, item.Source);
            Assert.Equal("B-04", item.FlagGridName);
            Assert.Equal("Example Honoree C", item.HonoreeName);
            Assert.NotNull(item.MergeAuditId);
            Assert.Contains("Example Honoree D", item.SourceDetail);
            Assert.Contains("who kept spot B-09", item.SourceDetail);
            Assert.Equal(5, await db.FlagGrids.Where(grid => grid.Id == 22).Select(grid => grid.HonoreeId).SingleAsync());
            Assert.Null(await db.FlagGrids.Where(grid => grid.Id == 21).Select(grid => grid.HonoreeId).SingleAsync());

            var auditId = item.MergeAuditId!.Value;
            var blocked = await HonoreeMergeAuditLog.UndoAsync(db, auditId, "admin@example.com", CancellationToken.None);
            Assert.Null(blocked);
            var canceled = await db.FlagRemovalQueueItems.SingleAsync(row => row.Id == item.Id);
            Assert.Equal(FlagRemovalQueue.MergeUndoneReason, canceled.CancelReason);
            Assert.NotNull(canceled.CanceledUtc);
        }

        await ResetAsync();
        int auditIdDone;
        int doneId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagGrids.Add(Grid(21, "B-04", now));
            db.FlagGrids.Add(Grid(22, "B-09", now));
            db.Honorees.Add(Person(4, "Example", "Honoree C", 21, now));
            db.Honorees.Add(Person(5, "Example", "Honoree D", 22, now));
            await db.SaveChangesAsync();
            db.FlagGrids.Single(grid => grid.Id == 21).HonoreeId = 4;
            db.FlagGrids.Single(grid => grid.Id == 22).HonoreeId = 5;
            await db.SaveChangesAsync();
            var confirmed = await db.Honorees.Where(honoree => honoree.Id == 4 || honoree.Id == 5).ToListAsync();
            await HonoreeMerge.ApplyAsync(
                db,
                confirmed.Single(honoree => honoree.Id == 5),
                confirmed,
                Fields(5),
                null,
                "admin@example.com",
                Guid.NewGuid(),
                CancellationToken.None);
            var item = await db.FlagRemovalQueueItems.SingleAsync();
            item.DoneUtc = now;
            item.DoneBy = "admin@example.com";
            await db.SaveChangesAsync();
            auditIdDone = item.MergeAuditId!.Value;
            doneId = item.Id;
            Assert.Null(await HonoreeMergeAuditLog.UndoAsync(db, auditIdDone, "admin@example.com", CancellationToken.None));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var rows = await db.FlagRemovalQueueItems.OrderBy(item => item.Id).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.NotNull(rows.Single(item => item.Id == doneId).DoneUtc);
            Assert.Null(rows.Single(item => item.Id == doneId).CanceledUtc);
            var putBack = rows.Single(item => item.Id != doneId);
            Assert.Equal(FlagRemovalQueue.Other, putBack.Source);
            Assert.StartsWith("Put back:", putBack.SourceDetail);
            Assert.Null(putBack.DoneUtc);
            Assert.Equal(auditIdDone, putBack.MergeAuditId);
        }
    }

    [Fact]
    public async Task Withdrawal_is_logged_already_done()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagGrids.Add(Grid(31, "E-15", now));
            db.Honorees.Add(Person(8, "Example", "Honoree G", 31, now));
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 80,
                FlagGridId = 31,
                HonoreeId = 8,
                ExternalUserObjectId = "submitter",
                ExternalUserEmail = "submitter@example.com",
                ClaimStatus = "Claimed",
                CreatedUtc = now
            });
            db.HonoreeChangeRequests.Add(Change(81, 80, 31, 8, "Withdrawal", now));
            await db.SaveChangesAsync();
            db.FlagGrids.Single(grid => grid.Id == 31).HonoreeId = 8;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var confirmed = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/81/removed-from-field"));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var open = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/removal-queue/count"));
        Assert.Equal(0, (await open.Content.ReadFromJsonAsync<CountPayload>())!.Count);
        var done = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/removal-queue?status=done"));
        var doneRows = await done.Content.ReadFromJsonAsync<List<QueueRow>>();
        Assert.NotNull(doneRows);
        var row = Assert.Single(doneRows);
        Assert.Equal(FlagRemovalQueue.Other, row.Source);
        Assert.Equal(FlagRemovalQueue.WithdrawalDetail, row.SourceDetail);
        Assert.True(row.LoggedAlreadyDone);
        Assert.Equal(row.CreatedUtc, row.DoneUtc);
    }

    [Fact]
    public async Task Approved_grid_change_frees_the_old_spot_and_the_same_grid_does_not()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagGrids.Add(Grid(41, "A-12", now));
            db.FlagGrids.Add(Grid(42, "B-04", now));
            db.Honorees.Add(Person(9, "Example", "Honoree A", 41, now));
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 90,
                HonoreeId = 9,
                FlagGridId = 41,
                ExternalUserObjectId = "submitter",
                ExternalUserEmail = "submitter@example.com",
                ClaimStatus = "Submitted",
                CreatedUtc = now
            });
            db.HonoreeChangeRequests.Add(Change(91, 90, 42, 9, "Submitted", now));
            await db.SaveChangesAsync();
            db.FlagGrids.Single(grid => grid.Id == 41).HonoreeId = 9;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var approved = await client.SendAsync(Admin(
            HttpMethod.Post,
            "/api/admin/review/91/approve",
            new { requiresCardReprint = false, reviewNotes = "Example approval" }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            Assert.Null(await db.FlagGrids.Where(grid => grid.Id == 41).Select(grid => grid.HonoreeId).SingleAsync());
            Assert.Equal(9, await db.FlagGrids.Where(grid => grid.Id == 42).Select(grid => grid.HonoreeId).SingleAsync());
            var item = Assert.Single(await db.FlagRemovalQueueItems.ToListAsync());
            Assert.Equal(FlagRemovalQueue.Move, item.Source);
            Assert.Equal("A-12", item.FlagGridName);
            Assert.Equal("B-04", item.NewFlagGridName);
            Assert.Contains("Approved change moved", item.SourceDetail);
        }

        await ResetAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagGrids.Add(Grid(41, "A-12", now));
            db.Honorees.Add(Person(9, "Example", "Honoree A", 41, now));
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 92,
                HonoreeId = 9,
                FlagGridId = 41,
                ExternalUserObjectId = "submitter",
                ExternalUserEmail = "submitter@example.com",
                ClaimStatus = "Submitted",
                CreatedUtc = now
            });
            db.HonoreeChangeRequests.Add(Change(93, 92, 41, 9, "Submitted", now));
            await db.SaveChangesAsync();
            db.FlagGrids.Single(grid => grid.Id == 41).HonoreeId = 9;
            await db.SaveChangesAsync();
        }

        var same = await client.SendAsync(Admin(
            HttpMethod.Post,
            "/api/admin/review/93/approve",
            new { requiresCardReprint = false, reviewNotes = "Same spot" }));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            Assert.Empty(await db.FlagRemovalQueueItems.ToListAsync());
            Assert.Equal(9, await db.FlagGrids.Where(grid => grid.Id == 41).Select(grid => grid.HonoreeId).SingleAsync());
        }
    }

    [Fact]
    public async Task Admin_apply_reprint_queues_the_old_spot_when_the_grid_changes()
    {
        await ResetAsync();
        var now = DateTime.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.FlagGrids.Add(Grid(51, "C-07", now));
            db.FlagGrids.Add(Grid(52, "D-03", now));
            db.Honorees.Add(Person(10, "Example", "Honoree B", 51, now));
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 100,
                HonoreeId = 10,
                FlagGridId = 51,
                ExternalUserObjectId = "admin-edit",
                ExternalUserEmail = "admin@example.com",
                ClaimStatus = "AdminDirectEdit",
                CreatedUtc = now
            });
            var draft = Change(101, 100, 52, 10, "Draft", now);
            draft.PhotoFileName = null;
            db.HonoreeChangeRequests.Add(draft);
            await db.SaveChangesAsync();
            db.FlagGrids.Single(grid => grid.Id == 51).HonoreeId = 10;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var applied = await client.SendAsync(Admin(HttpMethod.Post, "/api/flag-claims/100/admin-apply-reprint"));
        var body = await applied.Content.ReadAsStringAsync();
        Assert.True(applied.StatusCode == HttpStatusCode.OK, body);

        await using var scopeCheck = factory.Services.CreateAsyncScope();
        var saved = scopeCheck.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Null(await saved.FlagGrids.Where(grid => grid.Id == 51).Select(grid => grid.HonoreeId).SingleAsync());
        var item = Assert.Single(await saved.FlagRemovalQueueItems.ToListAsync());
        Assert.Equal("C-07", item.FlagGridName);
        Assert.Equal("D-03", item.NewFlagGridName);
        Assert.Contains("Approved change moved", item.SourceDetail);
    }

    [Fact]
    public async Task Bulk_done_skips_rows_already_finished_and_rejects_an_empty_selection()
    {
        await ResetAsync();
        await SeedHonoreeAsync(1, "Example", "Honoree A", 11, "A-12");
        await SeedHonoreeAsync(2, "Example", "Honoree B", 12, "B-04");
        var client = factory.CreateClient();
        await AssertSavedAsync(await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/11/clear")));
        await AssertSavedAsync(await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/review/flag-positions/12/clear")));

        List<int> ids;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            ids = await db.FlagRemovalQueueItems.Select(item => item.Id).OrderBy(id => id).ToListAsync();
        }

        var first = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/removal-queue/{ids[0]}/done"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<CountPayload>())!.Count);

        var both = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/removal-queue/done", new { ids }));
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);
        Assert.Equal(1, (await both.Content.ReadFromJsonAsync<CountPayload>())!.Count);

        var empty = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/removal-queue/done", new { ids = Array.Empty<int>() }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("Select at least one item to mark done.", await empty.Content.ReadAsStringAsync());

        var open = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/removal-queue?status=open"));
        Assert.Empty(await open.Content.ReadFromJsonAsync<List<QueueRow>>() ?? []);
    }

    [Fact]
    public async Task Count_is_admin_only_and_open_items_sort_by_spot()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        var anonymous = await client.GetAsync("/api/admin/removal-queue/count");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var visitor = new HttpRequestMessage(HttpMethod.Get, "/api/admin/removal-queue");
        visitor.Headers.Add(TestAuthHandler.OidHeader, "visitor");
        visitor.Headers.Add(TestAuthHandler.EmailHeader, "visitor@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(visitor)).StatusCode);

        var now = DateTime.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            foreach (var (id, name) in new (int, string)[] { (1, "C-07"), (2, "AA-10"), (3, "A-12"), (4, "B-04") })
            {
                db.FlagRemovalQueueItems.Add(new FlagRemovalQueueItem
                {
                    HonoreeName = $"Example Honoree {id}",
                    FlagGridId = id,
                    FlagGridName = name,
                    Source = FlagRemovalQueue.Manual,
                    CreatedUtc = now.AddMinutes(id),
                    CreatedBy = "admin@example.com"
                });
            }

            db.FlagRemovalQueueItems.Add(new FlagRemovalQueueItem
            {
                HonoreeName = "Example Honoree Done",
                FlagGridId = 5,
                FlagGridName = "Z-01",
                Source = FlagRemovalQueue.Manual,
                CreatedUtc = now.AddMinutes(-10),
                CreatedBy = "admin@example.com",
                DoneUtc = now.AddMinutes(-1),
                DoneBy = "admin@example.com"
            });
            db.FlagRemovalQueueItems.Add(new FlagRemovalQueueItem
            {
                HonoreeName = "Example Honoree Older",
                FlagGridId = 6,
                FlagGridName = "Z-02",
                Source = FlagRemovalQueue.Manual,
                CreatedUtc = now.AddHours(-2),
                CreatedBy = "admin@example.com",
                DoneUtc = now.AddHours(-1),
                DoneBy = "admin@example.com"
            });
            await db.SaveChangesAsync();
        }

        var count = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/removal-queue/count"));
        Assert.Equal(4, (await count.Content.ReadFromJsonAsync<CountPayload>())!.Count);
        var open = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/removal-queue?status=open"));
        var names = (await open.Content.ReadFromJsonAsync<List<QueueRow>>())!.Select(row => row.FlagGridName).ToArray();
        Assert.Equal(new[] { "A-12", "B-04", "C-07", "AA-10" }, names);

        var done = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/removal-queue?status=done"));
        var history = (await done.Content.ReadFromJsonAsync<List<QueueRow>>())!.Select(row => row.FlagGridName).ToArray();
        Assert.Equal(new[] { "Z-01", "Z-02" }, history);
    }

    [Fact]
    public void Queue_writes_stay_in_application_code()
    {
        var apiRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PFOH.Api"));
        var service = File.ReadAllText(Path.Combine(apiRoot, "Services", "FlagRemovalQueue.cs"));
        var schema = File.ReadAllText(Path.Combine(apiRoot, "Services", "FlagRemovalQueueSchema.cs"));
        var program = File.ReadAllText(Path.Combine(apiRoot, "Program.cs"));
        Assert.Contains("must call", service, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Record", service, StringComparison.Ordinal);
        Assert.Contains("No database trigger", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TRIGGER", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FlagRemovalQueueSchema.EnsureAsync", program, StringComparison.Ordinal);
    }

    private async Task AssertSavedAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("PDF", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.FlagRemovalQueueItems.RemoveRange(db.FlagRemovalQueueItems);
        db.HonoreeMergeAudits.RemoveRange(db.HonoreeMergeAudits);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        await db.SaveChangesAsync();
    }

    private async Task SeedHonoreeAsync(int id, string first, string last, int? gridId, string? gridName)
    {
        var now = DateTime.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        if (gridId is int grid && gridName is not null && !await db.FlagGrids.AnyAsync(item => item.Id == grid))
        {
            db.FlagGrids.Add(Grid(grid, gridName, now));
        }

        if (!await db.Honorees.AnyAsync(item => item.Id == id))
        {
            db.Honorees.Add(Person(id, first, last, gridId, now));
        }

        await db.SaveChangesAsync();
        if (gridId is int linked)
        {
            db.FlagGrids.Single(item => item.Id == linked).HonoreeId = id;
            await db.SaveChangesAsync();
        }
    }

    private async Task SeedGridAsync(int id, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        if (await db.FlagGrids.AnyAsync(item => item.Id == id))
        {
            return;
        }

        db.FlagGrids.Add(Grid(id, name, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private static FlagGrid Grid(int id, string name, DateTime now) => new()
    {
        Id = id,
        FlagGridName = name,
        Notes = string.Empty,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = now,
        ModifiedDate = now
    };

    private static Honoree Person(int id, string first, string last, int? gridId, DateTime now) => new()
    {
        Id = id,
        FirstName = first,
        LastName = last,
        Rank = string.Empty,
        Description = "Example tribute",
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
    };

    private static HonoreeChangeRequest Change(int id, int claimId, int? gridId, int? honoreeId, string status, DateTime now) => new()
    {
        Id = id,
        FlagClaimId = claimId,
        FlagGridId = gridId,
        HonoreeId = honoreeId,
        FirstName = "Example",
        LastName = "Honoree A",
        Description = "Example tribute",
        RequestStatus = status,
        CreatedUtc = now,
        SubmittedUtc = now
    };

    private static Dictionary<string, int> Fields(int survivor) => new()
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

    private static HttpRequestMessage Admin(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Example Admin 1");
        request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private sealed record CountPayload(int Count);

    private sealed record QueueRow(
        int Id,
        string FlagGridName,
        string Source,
        string? SourceDetail,
        string? CancelReason,
        bool LoggedAlreadyDone,
        DateTime CreatedUtc,
        DateTime? DoneUtc);
}
