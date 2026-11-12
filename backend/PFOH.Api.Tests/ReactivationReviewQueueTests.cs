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

public class ReactivationReviewQueueTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Search_returns_only_inactive_records_with_no_flag()
    {
        await ResetAsync();
        var army = await SeedBranchAsync("Army");
        var navy = await SeedBranchAsync("Navy");
        var flaggedGrid = await SeedGridAsync("Q-40");
        var pointedGrid = await SeedGridAsync("D-04");
        var ada = await SeedAsync("Ada", "Lovelace", army, legacyKey: 88011, reason: "Moved away", when: new DateTime(2024, 5, 1, 15, 0, 0, DateTimeKind.Utc), who: "remover@example.test");
        var grace = await SeedAsync("Grace", "Hopper", navy, reason: "Left the field", when: new DateTime(2024, 6, 2, 15, 0, 0, DateTimeKind.Utc), who: "remover@example.test");
        await SeedAsync("Alan", "Turing", army);
        await SeedAsync("Lin", "Marcus", army, flagGridId: flaggedGrid, keepFlag: true);
        var pointed = await SeedAsync("Pat", "Quinn", navy);
        await PointGridAtAsync(pointedGrid, pointed);
        await SeedAsync("Casey", "Nguyen", army, active: true);

        var client = factory.CreateClient();
        var all = await ReadListAsync(client, "/api/admin/reactivation/inactive");
        Assert.Equal(["Grace Hopper", "Ada Lovelace", "Alan Turing"], all.Items.Select(item => item.Name).ToArray());
        Assert.DoesNotContain(all.Items, item => item.Name is "Lin Marcus" or "Pat Quinn" or "Casey Nguyen");

        var adaRow = all.Items.Single(item => item.Name == "Ada Lovelace");
        Assert.Equal(ada, adaRow.HonoreeId);
        Assert.Equal(88011, adaRow.LegacyKey);
        Assert.Equal("Army", adaRow.Branch);
        Assert.Equal("Moved away", adaRow.Reason);
        Assert.Equal("remover@example.test", adaRow.DeactivatedBy);
        Assert.Equal(2024, adaRow.DeactivatedUtc?.Year);
        Assert.True(adaRow.CanReactivate);

        var alan = all.Items.Single(item => item.Name == "Alan Turing");
        Assert.Equal(InactiveHonoreeAdminService.NotRecorded, alan.Reason);
        Assert.Equal(InactiveHonoreeAdminService.NotRecorded, alan.DeactivatedBy);
        Assert.Null(alan.DeactivatedUtc);

        var byName = await ReadListAsync(client, "/api/admin/reactivation/inactive?q=hopper");
        Assert.Equal(["Grace Hopper"], byName.Items.Select(item => item.Name).ToArray());
        Assert.Equal("Navy", byName.Items[0].Branch);

        var byBranch = await ReadListAsync(client, "/api/admin/reactivation/inactive?q=Army");
        Assert.Equal(["Ada Lovelace", "Alan Turing"], byBranch.Items.Select(item => item.Name).ToArray());

        var byId = await ReadListAsync(client, $"/api/admin/reactivation/inactive?q={grace}");
        Assert.Equal(["Grace Hopper"], byId.Items.Select(item => item.Name).ToArray());

        var byLegacy = await ReadListAsync(client, "/api/admin/reactivation/inactive?q=88011");
        Assert.Equal(["Ada Lovelace"], byLegacy.Items.Select(item => item.Name).ToArray());

        var flaggedByName = await ReadListAsync(client, "/api/admin/reactivation/inactive?q=Marcus");
        Assert.Empty(flaggedByName.Items);
        var activeByName = await ReadListAsync(client, "/api/admin/reactivation/inactive?q=Nguyen");
        Assert.Empty(activeByName.Items);

        var page = await ReadListAsync(client, "/api/admin/reactivation/inactive?page=2&pageSize=1");
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Page);
        Assert.Equal(["Ada Lovelace"], page.Items.Select(item => item.Name).ToArray());
    }

    [Fact]
    public async Task Add_to_review_queue_submits_with_no_flag_audit_and_no_link()
    {
        await ResetAsync();
        var honoreeId = await SeedAsync("Ada", "Lovelace", null, reason: "Moved away", when: DateTime.UtcNow, who: "remover@example.test");
        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId } }
        }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("was added to the review queue", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/reactivate/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.False(honoree.IsActive);
        Assert.Null(honoree.FlagGridId);
        var change = await db.HonoreeChangeRequests.SingleAsync(row => row.HonoreeId == honoreeId);
        Assert.Equal("Submitted", change.RequestStatus);
        Assert.Null(change.FlagGridId);
        Assert.Equal(InactiveHonoreeAdminService.ReactivationPageQueueNote, change.ReviewNotes);
        var audit = await db.FlagGridRemovalAudits.SingleAsync(row => row.HonoreeId == honoreeId && row.Action == "Restore");
        Assert.Equal(InactiveHonoreeAdminService.ReactivationPageQueueNote, audit.Reason);
        Assert.Null(audit.FlagGridId);
        Assert.Equal("admin@example.test", audit.ActorEmail);
        Assert.Equal("admin-1", audit.ActorObjectId);
        Assert.Equal(DateTime.UtcNow.Year, audit.OccurredUtc.Year);
        Assert.Equal(0, await db.HonoreeReactivationTokens.CountAsync());
        Assert.Equal(0, await db.HonoreeReactivationRequests.CountAsync());

        var pending = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/review/pending"));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Contains("Ada Lovelace", await pending.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var again = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId } }
        }));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already in the review queue", await again.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));
        Assert.Equal(1, await db.HonoreeChangeRequests.CountAsync(row => row.HonoreeId == honoreeId && row.RequestStatus == "Submitted"));
    }

    [Fact]
    public async Task Queued_record_can_be_approved_and_then_assigned()
    {
        await ResetAsync();
        var honoreeId = await SeedAsync("Ada", "Lovelace", null, reason: "Moved away", when: DateTime.UtcNow, who: "remover@example.test");
        var flagId = await SeedGridAsync("Q-40");
        var client = factory.CreateClient();
        var queued = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId } }
        }));
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        var payload = await queued.Content.ReadFromJsonAsync<ReactivatePayload>(Json);
        var changeId = payload!.Results[0].ChangeRequestId;

        var approved = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/review/{changeId}/approve", new
        {
            requiresCardReprint = false,
            reviewNotes = "Approved from the queue."
        }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
            Assert.True(honoree.IsActive);
            Assert.Null(honoree.FlagGridId);
        }

        var assigned = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/review/flag-positions/{flagId}/assign", new
        {
            honoreeId
        }));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Contains("Q-40", await assigned.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var after = factory.Services.CreateAsyncScope();
        var database = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        var placed = await database.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.True(placed.IsActive);
        Assert.Equal(flagId, placed.FlagGridId);
        Assert.Equal(honoreeId, (await database.FlagGrids.SingleAsync(grid => grid.Id == flagId)).HonoreeId);
        Assert.Equal(0, await database.HonoreeReactivationTokens.CountAsync());
    }

    [Fact]
    public async Task Multi_select_adds_each_record_to_the_review_queue()
    {
        await ResetAsync();
        var ada = await SeedAsync("Ada", "Lovelace", null, reason: "Moved away", when: DateTime.UtcNow, who: "remover@example.test");
        var alan = await SeedAsync("Alan", "Turing", null);
        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId = ada }, new { honoreeId = alan } }
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ReactivatePayload>(Json);
        Assert.Equal(2, payload!.Results.Count);
        Assert.All(payload.Results, item => Assert.Equal("reactivated", item.Outcome));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(2, await db.HonoreeChangeRequests.CountAsync(row => row.RequestStatus == "Submitted" && row.FlagGridId == null));
        Assert.Equal(2, await db.FlagGridRemovalAudits.CountAsync(row =>
            row.Action == "Restore" &&
            row.FlagGridId == null &&
            row.Reason == InactiveHonoreeAdminService.ReactivationPageQueueNote));
        Assert.Equal(0, await db.HonoreeReactivationTokens.CountAsync());
        Assert.Equal(0, await db.HonoreeReactivationRequests.CountAsync());

        var pending = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/review/pending"));
        var pendingBody = await pending.Content.ReadAsStringAsync();
        Assert.Contains("Ada Lovelace", pendingBody, StringComparison.Ordinal);
        Assert.Contains("Alan Turing", pendingBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_already_active_record_is_refused()
    {
        await ResetAsync();
        var honoreeId = await SeedAsync("Ada", "Lovelace", null, active: true);
        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId } }
        }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already active", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var after = factory.Services.CreateAsyncScope();
        var db = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.True((await db.Honorees.SingleAsync(row => row.Id == honoreeId)).IsActive);
        Assert.Equal(0, await db.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));
        Assert.Equal(0, await db.HonoreeChangeRequests.CountAsync(row => row.HonoreeId == honoreeId));
    }

    [Fact]
    public async Task An_already_queued_record_is_refused()
    {
        await ResetAsync();
        var honoreeId = await SeedAsync("Ada", "Lovelace", null, reason: "Moved away", when: DateTime.UtcNow, who: "remover@example.test");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            var claim = new FlagClaim
            {
                HonoreeId = honoreeId,
                FlagGridId = null,
                ExternalUserObjectId = "seed",
                ExternalUserEmail = "seed@example.test",
                ClaimStatus = "Submitted",
                CreatedUtc = now,
                SubmittedUtc = now
            };
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                FlagClaim = claim,
                HonoreeId = honoreeId,
                FlagGridId = null,
                FirstName = "Ada",
                LastName = "Lovelace",
                RequestStatus = "Submitted",
                CreatedUtc = now,
                SubmittedUtc = now
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var list = await ReadListAsync(client, "/api/admin/reactivation/inactive?q=Lovelace");
        Assert.False(list.Items[0].CanReactivate);
        Assert.Contains("review queue", list.Items[0].BlockReason, StringComparison.OrdinalIgnoreCase);

        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId } }
        }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already in the review queue", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var after = factory.Services.CreateAsyncScope();
        var database = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(0, await database.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));
        Assert.Equal(1, await database.HonoreeChangeRequests.CountAsync(row => row.HonoreeId == honoreeId && row.RequestStatus == "Submitted"));
    }

    [Fact]
    public async Task A_merged_record_is_refused()
    {
        await ResetAsync();
        var survivor = await SeedAsync("Grace", "Hopper", null, active: true);
        var absorbed = await SeedAsync("Ada", "Lovelace", null);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var when = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            db.HonoreeMergeAudits.Add(new HonoreeMergeAudit
            {
                SurvivorId = survivor,
                AbsorbedHonoreeIds = $"[{absorbed}]",
                BeforeState = "{}",
                AfterSurvivorState = "{}",
                AfterState = "{}",
                CreatedHonoreeClaimIds = "[]",
                MergedBy = "merger@example.test",
                MergedUtc = when,
                SurvivorModifiedUtc = when
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var list = await ReadListAsync(client, "/api/admin/reactivation/inactive?q=Lovelace");
        Assert.False(list.Items[0].CanReactivate);
        Assert.Contains("Merged into Grace Hopper.", list.Items[0].Reason, StringComparison.Ordinal);

        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId = absorbed } }
        }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Merge history", body, StringComparison.Ordinal);
        Assert.Contains("Grace Hopper", body, StringComparison.Ordinal);

        await using var after = factory.Services.CreateAsyncScope();
        var database = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.False((await database.Honorees.SingleAsync(row => row.Id == absorbed)).IsActive);
        Assert.Equal(0, await database.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));
    }

    [Fact]
    public async Task A_record_with_a_flag_is_refused()
    {
        await ResetAsync();
        var gridId = await SeedGridAsync("B-12");
        var kept = await SeedAsync("Ada", "Lovelace", null, flagGridId: gridId, keepFlag: true);
        var pointedGrid = await SeedGridAsync("Q-17");
        var pointed = await SeedAsync("Alan", "Turing", null);
        await PointGridAtAsync(pointedGrid, pointed);

        var client = factory.CreateClient();
        var listed = await ReadListAsync(client, "/api/admin/reactivation/inactive");
        Assert.Empty(listed.Items);

        var keptResponse = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId = kept } }
        }));
        Assert.Equal(HttpStatusCode.Conflict, keptResponse.StatusCode);
        Assert.Contains("has a flag", await keptResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var pointedResponse = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId = pointed } }
        }));
        Assert.Equal(HttpStatusCode.Conflict, pointedResponse.StatusCode);
        Assert.Contains("has a flag", await pointedResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var after = factory.Services.CreateAsyncScope();
        var db = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(gridId, (await db.Honorees.SingleAsync(row => row.Id == kept)).FlagGridId);
        Assert.Equal(kept, (await db.FlagGrids.SingleAsync(row => row.Id == gridId)).HonoreeId);
        Assert.Equal(pointed, (await db.FlagGrids.SingleAsync(row => row.Id == pointedGrid)).HonoreeId);
        Assert.Equal(0, await db.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));
        Assert.Equal(0, await db.HonoreeChangeRequests.CountAsync());
        Assert.Equal(0, await db.HonoreeReactivationTokens.CountAsync());
    }

    [Fact]
    public async Task A_missing_record_is_not_found()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/reactivation/review-queue", new
        {
            items = new[] { new { honoreeId = 404 } }
        }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not found", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_admins_cannot_search_or_add()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/reactivation/inactive")).StatusCode);
        var anonymousPost = new HttpRequestMessage(HttpMethod.Post, "/api/admin/reactivation/review-queue")
        {
            Content = JsonContent.Create(new { items = new[] { new { honoreeId = 1 } } })
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonymousPost)).StatusCode);

        var visitor = new HttpRequestMessage(HttpMethod.Get, "/api/admin/reactivation/inactive");
        visitor.Headers.Add(TestAuthHandler.OidHeader, "visitor");
        visitor.Headers.Add(TestAuthHandler.EmailHeader, "visitor@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(visitor)).StatusCode);

        var visitorPost = new HttpRequestMessage(HttpMethod.Post, "/api/admin/reactivation/review-queue")
        {
            Content = JsonContent.Create(new { items = new[] { new { honoreeId = 1 } } })
        };
        visitorPost.Headers.Add(TestAuthHandler.OidHeader, "visitor");
        visitorPost.Headers.Add(TestAuthHandler.EmailHeader, "visitor@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(visitorPost)).StatusCode);

        var adminRole = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/reactivation/inactive", roles: "Admin"));
        Assert.Equal(HttpStatusCode.OK, adminRole.StatusCode);
    }

    [Fact]
    public void Queue_code_does_not_send_email_or_build_a_reactivation_link()
    {
        var root = FindRepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "backend", "PFOH.Api", "Services", "InactiveHonoreeAdminService.cs"));
        Assert.DoesNotContain("SmtpClient", service, StringComparison.Ordinal);
        Assert.DoesNotContain("MailMessage", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SendGrid", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMail", service, StringComparison.Ordinal);
        Assert.DoesNotContain("HonoreeReactivationToken", service, StringComparison.Ordinal);
        Assert.DoesNotContain("/reactivate/", service, StringComparison.Ordinal);
        Assert.Contains("AddToReviewQueueAsync", service, StringComparison.Ordinal);
    }

    private async Task<ListPayload> ReadListAsync(HttpClient client, string url)
    {
        var response = await client.SendAsync(Admin(HttpMethod.Get, url));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListPayload>(Json);
        return payload ?? throw new InvalidOperationException("The list response was empty.");
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.HonoreeReactivationRequests.RemoveRange(db.HonoreeReactivationRequests);
        db.HonoreeReactivationTokens.RemoveRange(db.HonoreeReactivationTokens);
        db.ReprintQueueEvents.RemoveRange(db.ReprintQueueEvents);
        db.FlagGridRemovalAudits.RemoveRange(db.FlagGridRemovalAudits);
        db.FlagRemovalQueueItems.RemoveRange(db.FlagRemovalQueueItems);
        db.HonoreeMergeAudits.RemoveRange(db.HonoreeMergeAudits);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.ServiceBranches.RemoveRange(db.ServiceBranches);
        db.ServiceBranchCategories.RemoveRange(db.ServiceBranchCategories);
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedBranchAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        var category = new ServiceBranchCategory
        {
            ServiceBranchCategoryName = name,
            Description = name,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        var branch = new ServiceBranch
        {
            ServiceBranchCategory = category,
            ServiceBranchName = name,
            Description = name,
            LogoFileName = string.Empty,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.ServiceBranches.Add(branch);
        await db.SaveChangesAsync();
        return branch.Id;
    }

    private async Task<int> SeedGridAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        var grid = new FlagGrid
        {
            FlagGridName = name,
            Notes = string.Empty,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.FlagGrids.Add(grid);
        await db.SaveChangesAsync();
        return grid.Id;
    }

    private async Task PointGridAtAsync(int gridId, int honoreeId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var grid = await db.FlagGrids.SingleAsync(row => row.Id == gridId);
        grid.HonoreeId = honoreeId;
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedAsync(
        string first,
        string last,
        int? branchId,
        int? legacyKey = null,
        int? flagGridId = null,
        bool keepFlag = false,
        bool active = false,
        string? reason = null,
        DateTime? when = null,
        string? who = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = when ?? DateTime.UtcNow;
        var honoree = new Honoree
        {
            FirstName = first,
            LastName = last,
            Rank = "Sergeant",
            ServiceBranchId = branchId,
            LegacyKey = legacyKey,
            Description = "A clearly fake tribute.",
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = string.Empty,
            PhoneNumber = string.Empty,
            EmailAddress = string.Empty,
            Awards = string.Empty,
            ConflictsServed = string.Empty,
            DatesUserEntry = string.Empty,
            IsActive = active,
            FlagGridId = keepFlag ? flagGridId : null,
            DeletedDate = active || when is null ? null : now,
            DeletedBy = active || who is null ? null : who,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.Honorees.Add(honoree);
        await db.SaveChangesAsync();
        if (keepFlag && flagGridId is int gridId)
        {
            var grid = await db.FlagGrids.SingleAsync(row => row.Id == gridId);
            grid.HonoreeId = honoree.Id;
        }

        if (reason is not null)
        {
            db.FlagGridRemovalAudits.Add(new FlagGridRemovalAudit
            {
                HonoreeId = honoree.Id,
                FlagGridId = null,
                Action = "Remove",
                ActorEmail = who ?? "remover@example.test",
                OccurredUtc = now,
                Reason = reason,
                BeforeState = "{}",
                AfterState = "{}"
            });
        }

        await db.SaveChangesAsync();
        return honoree.Id;
    }

    private static HttpRequestMessage Admin(HttpMethod method, string url, object? body = null, string roles = "PFOH.Admin")
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.test");
        request.Headers.Add(TestAuthHandler.NameHeader, "Admin");
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("VERSION was not found.");
    }

    private sealed record ListPayload(List<ListItem> Items, int Page, int PageSize, int Total);

    private sealed record ListItem(
        int HonoreeId,
        string Name,
        string? Branch,
        DateTime? DeactivatedUtc,
        string DeactivatedBy,
        string Reason,
        bool CanReactivate,
        string? BlockReason,
        int? LegacyKey);

    private sealed record ReactivatePayload(List<ReactivateItem> Results);

    private sealed record ReactivateItem(
        int HonoreeId,
        string Outcome,
        string Code,
        string Message,
        int? ChangeRequestId);
}
