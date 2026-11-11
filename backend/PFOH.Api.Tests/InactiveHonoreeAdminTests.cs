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

public class InactiveHonoreeAdminTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Flag_labels_match_the_field_shorthand()
    {
        Assert.True(FlagPositionLabel.Matches("C11", "C-11"));
        Assert.True(FlagPositionLabel.Matches("c-11", "C-11"));
        Assert.True(FlagPositionLabel.Matches("AA10", "AA-10"));
        Assert.False(FlagPositionLabel.Matches("C11", "C-01"));
        Assert.False(FlagPositionLabel.Matches("Navy", "C-11"));
        Assert.Contains("UX_Honorees_OneActiveFlagGrid", HonoreeActiveFlagSlotSchema.EnsureSql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE ", HonoreeActiveFlagSlotSchema.EnsureSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", HonoreeActiveFlagSlotSchema.EnsureSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER COLUMN [FlagGridId] int NULL", FlagGridRemovalAuditSchema.AlterFlagGridIdNullableSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_filters_by_flag_name_and_branch_and_shows_not_recorded()
    {
        await ResetAsync();
        var army = await SeedBranchAsync("Army");
        var navy = await SeedBranchAsync("Navy");
        var c11 = await SeedGridAsync("C-11");
        var d04 = await SeedGridAsync("D-04");
        await SeedInactiveAsync("Ada", "Lovelace", army, c11, "Moved away", new DateTime(2024, 5, 1, 15, 0, 0, DateTimeKind.Utc), "remover@example.test");
        await SeedInactiveAsync("Grace", "Hopper", navy, d04, "Left the field", new DateTime(2024, 6, 1, 15, 0, 0, DateTimeKind.Utc), "remover@example.test");
        await SeedInactiveAsync("Alan", "Turing", army, null, null, null, null);

        var client = factory.CreateClient();
        var byFlag = await ReadListAsync(client, "/api/admin/inactive-honorees?q=C11");
        Assert.Equal(["Ada Lovelace"], byFlag.Items.Select(item => item.Name).ToArray());
        Assert.Equal("C-11", byFlag.Items[0].FormerFlagGridName);
        Assert.Equal("Moved away", byFlag.Items[0].Reason);
        Assert.Equal("remover@example.test", byFlag.Items[0].DeactivatedBy);
        Assert.Equal(2024, byFlag.Items[0].DeactivatedUtc?.Year);
        Assert.Equal("free", byFlag.Items[0].SlotStatus);

        var byName = await ReadListAsync(client, "/api/admin/inactive-honorees?q=hopper");
        Assert.Equal(["Grace Hopper"], byName.Items.Select(item => item.Name).ToArray());
        Assert.Equal("Navy", byName.Items[0].Branch);

        var byBranch = await ReadListAsync(client, "/api/admin/inactive-honorees?q=Army");
        Assert.Equal(["Ada Lovelace", "Alan Turing"], byBranch.Items.Select(item => item.Name).ToArray());
        var turing = byBranch.Items.Single(item => item.Name == "Alan Turing");
        Assert.Equal(InactiveHonoreeAdminService.NotRecorded, turing.Reason);
        Assert.Equal(InactiveHonoreeAdminService.NotRecorded, turing.DeactivatedBy);
        Assert.Null(turing.DeactivatedUtc);
        Assert.Equal("none", turing.SlotStatus);
        Assert.True(turing.CanReactivate);

        var page = await ReadListAsync(client, "/api/admin/inactive-honorees?page=2&pageSize=1");
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Page);
        Assert.Equal(["Ada Lovelace"], page.Items.Select(item => item.Name).ToArray());
    }

    [Fact]
    public async Task Reactivate_restores_the_open_flag_and_writes_an_audit()
    {
        await ResetAsync();
        var gridId = await SeedGridAsync("C-11");
        var honoreeId = await SeedInactiveAsync("Ada", "Lovelace", null, gridId, "Moved away", DateTime.UtcNow, "remover@example.test", openRemoval: true);

        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate", new
        {
            items = new[] { new { honoreeId, flagGridId = (int?)null } }
        }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonSerializer.Deserialize<ReactivatePayload>(body, Json);
        Assert.NotNull(payload);
        Assert.Equal("reactivated", payload.Results[0].Outcome);
        Assert.Contains("Ada Lovelace is active on flag C-11.", payload.Results[0].Message, StringComparison.Ordinal);
        Assert.True(payload.Results[0].RemovalCanceled);
        Assert.False(payload.Results[0].ReprintQueued);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.True(honoree.IsActive);
        Assert.Null(honoree.DeletedDate);
        Assert.Equal(gridId, honoree.FlagGridId);
        var grid = await db.FlagGrids.SingleAsync(row => row.Id == gridId);
        Assert.Equal(honoreeId, grid.HonoreeId);
        var audit = await db.FlagGridRemovalAudits.SingleAsync(row => row.HonoreeId == honoreeId && row.Action == "Restore");
        Assert.Equal("admin@example.test", audit.ActorEmail);
        Assert.Equal("admin-1", audit.ActorObjectId);
        Assert.Equal(gridId, audit.FlagGridId);
        Assert.Contains("\"isActive\":false", audit.BeforeState, StringComparison.Ordinal);
        Assert.Contains("\"isActive\":true", audit.AfterState, StringComparison.Ordinal);
        Assert.Contains("\"flagGridId\":" + gridId, audit.AfterState, StringComparison.Ordinal);
        Assert.True(audit.OccurredUtc > DateTime.UtcNow.AddMinutes(-5));
        var removal = await db.FlagRemovalQueueItems.SingleAsync(row => row.HonoreeId == honoreeId);
        Assert.NotNull(removal.CanceledUtc);
        Assert.Equal(0, await db.HonoreeChangeRequests.CountAsync(row => row.HonoreeId == honoreeId && row.RequiresCardReprint && row.CardPrintedUtc == null));
    }

    [Fact]
    public async Task Taken_flag_is_blocked_until_an_open_flag_is_chosen()
    {
        await ResetAsync();
        var takenId = await SeedGridAsync("C-11");
        var openId = await SeedGridAsync("D-04");
        await SeedInactiveAsync("Grace", "Hopper", null, takenId, "Holds the flag", DateTime.UtcNow, "remover@example.test", active: true);
        var honoreeId = await SeedInactiveAsync("Ada", "Lovelace", null, takenId, "Moved away", DateTime.UtcNow, "remover@example.test");

        var client = factory.CreateClient();
        var blocked = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate", new
        {
            items = new[] { new { honoreeId, flagGridId = (int?)null } }
        }));
        var blockedBody = await blocked.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("assigned to Grace Hopper", blockedBody, StringComparison.Ordinal);
        Assert.Contains("Choose an open flag", blockedBody, StringComparison.Ordinal);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
            Assert.False(honoree.IsActive);
            Assert.Null(honoree.FlagGridId);
            Assert.Equal(0, await db.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));
        }

        var restored = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate", new
        {
            items = new[] { new { honoreeId, flagGridId = openId } }
        }));
        var restoredBody = await restored.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Contains("Ada Lovelace is active on flag D-04.", restoredBody, StringComparison.Ordinal);
        Assert.Contains("print queue", restoredBody, StringComparison.Ordinal);

        await using var after = factory.Services.CreateAsyncScope();
        var database = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        var ada = await database.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.True(ada.IsActive);
        Assert.Equal(openId, ada.FlagGridId);
        Assert.Equal(honoreeId, (await database.FlagGrids.SingleAsync(row => row.Id == openId)).HonoreeId);
        Assert.Equal(takenId, (await database.Honorees.SingleAsync(row => row.FirstName == "Grace")).FlagGridId);
        var audit = await database.FlagGridRemovalAudits.SingleAsync(row => row.Action == "Restore");
        Assert.Equal("admin@example.test", audit.ActorEmail);
        Assert.Equal(openId, audit.FlagGridId);
        Assert.Contains("\"isActive\":false", audit.BeforeState, StringComparison.Ordinal);
        Assert.Contains("\"isActive\":true", audit.AfterState, StringComparison.Ordinal);
        Assert.True(await database.HonoreeChangeRequests.AnyAsync(row =>
            row.HonoreeId == honoreeId &&
            row.FlagGridId == openId &&
            row.RequiresCardReprint &&
            row.CardPrintedUtc == null &&
            row.RequestStatus == "Approved"));
    }

    [Fact]
    public async Task Multi_select_reports_each_honoree()
    {
        await ResetAsync();
        var openId = await SeedGridAsync("D-04");
        var takenId = await SeedGridAsync("C-11");
        var ada = await SeedInactiveAsync("Ada", "Lovelace", null, openId, "Moved away", DateTime.UtcNow, "remover@example.test");
        await SeedInactiveAsync("Grace", "Hopper", null, takenId, "Holds the flag", DateTime.UtcNow, "remover@example.test", active: true);
        var alan = await SeedInactiveAsync("Alan", "Turing", null, takenId, "Moved away", DateTime.UtcNow, "remover@example.test");

        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate", new
        {
            items = new[]
            {
                new { honoreeId = ada, flagGridId = (int?)null },
                new { honoreeId = alan, flagGridId = (int?)null }
            }
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ReactivatePayload>(Json);
        Assert.NotNull(payload);
        Assert.Equal(2, payload.Results.Count);
        Assert.Equal("reactivated", payload.Results.Single(row => row.HonoreeId == ada).Outcome);
        var blocked = payload.Results.Single(row => row.HonoreeId == alan);
        Assert.Equal("blocked", blocked.Outcome);
        Assert.Equal("slot-conflict", blocked.Code);
        Assert.Contains("assigned to Grace Hopper", blocked.Message, StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.True((await db.Honorees.SingleAsync(row => row.Id == ada)).IsActive);
        Assert.False((await db.Honorees.SingleAsync(row => row.Id == alan)).IsActive);
        Assert.Equal(1, await db.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));
    }

    [Fact]
    public async Task A_merged_honoree_is_listed_and_cannot_be_restored()
    {
        await ResetAsync();
        var survivor = await SeedInactiveAsync("Grace", "Hopper", null, null, null, null, null, active: true);
        var absorbed = await SeedInactiveAsync("Ada", "Lovelace", null, null, null, null, null);
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
        var list = await ReadListAsync(client, "/api/admin/inactive-honorees?q=Lovelace");
        Assert.Equal("merged", list.Items[0].SlotStatus);
        Assert.False(list.Items[0].CanReactivate);
        Assert.Contains("Merged into Grace Hopper.", list.Items[0].Reason, StringComparison.Ordinal);

        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate", new
        {
            items = new[] { new { honoreeId = absorbed, flagGridId = (int?)null } }
        }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Merge history", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var after = factory.Services.CreateAsyncScope();
        var database = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.False((await database.Honorees.SingleAsync(row => row.Id == absorbed)).IsActive);
        Assert.Equal(0, await database.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));
    }

    [Fact]
    public async Task Non_admins_cannot_list_or_restore()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/inactive-honorees")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/inactive-honorees/free-slots")).StatusCode);
        var anonymousPost = new HttpRequestMessage(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate")
        {
            Content = JsonContent.Create(new { items = new[] { new { honoreeId = 1 } } })
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonymousPost)).StatusCode);

        var visitor = new HttpRequestMessage(HttpMethod.Get, "/api/admin/inactive-honorees");
        visitor.Headers.Add(TestAuthHandler.OidHeader, "visitor");
        visitor.Headers.Add(TestAuthHandler.EmailHeader, "visitor@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(visitor)).StatusCode);

        var visitorPost = new HttpRequestMessage(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate")
        {
            Content = JsonContent.Create(new { items = new[] { new { honoreeId = 1 } } })
        };
        visitorPost.Headers.Add(TestAuthHandler.OidHeader, "visitor");
        visitorPost.Headers.Add(TestAuthHandler.EmailHeader, "visitor@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(visitorPost)).StatusCode);

        var adminRole = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/inactive-honorees", roles: "Admin"));
        Assert.Equal(HttpStatusCode.OK, adminRole.StatusCode);
    }

    [Fact]
    public async Task Honoree_with_no_flag_is_restored_without_a_slot()
    {
        await ResetAsync();
        var honoreeId = await SeedInactiveAsync("Alan", "Turing", null, null, null, null, null);
        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate", new
        {
            items = new[] { new { honoreeId, flagGridId = (int?)null } }
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ReactivatePayload>(Json);
        Assert.Equal("no-slot", payload!.Results[0].Code);
        Assert.Contains("no flag position", payload.Results[0].Message, StringComparison.Ordinal);
        Assert.False(payload.Results[0].ReprintQueued);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.True(honoree.IsActive);
        Assert.Null(honoree.FlagGridId);
        var audit = await db.FlagGridRemovalAudits.SingleAsync();
        Assert.Null(audit.FlagGridId);
        Assert.Equal("admin@example.test", audit.ActorEmail);
        Assert.Contains("\"isActive\":false", audit.BeforeState, StringComparison.Ordinal);
        Assert.Contains("\"isActive\":true", audit.AfterState, StringComparison.Ordinal);
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

    private async Task<int> SeedGridAsync(string name, bool reserved = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        var grid = new FlagGrid
        {
            FlagGridName = name,
            Reserved = reserved,
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

    private async Task<int> SeedInactiveAsync(
        string first,
        string last,
        int? branchId,
        int? formerGridId,
        string? reason,
        DateTime? when,
        string? who,
        bool active = false,
        bool openRemoval = false)
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
            FlagGridId = active ? formerGridId : null,
            DeletedDate = active || when is null ? null : now,
            DeletedBy = active || who is null ? null : who,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.Honorees.Add(honoree);
        await db.SaveChangesAsync();
        if (formerGridId is int gridId)
        {
            var grid = await db.FlagGrids.SingleAsync(row => row.Id == gridId);
            if (active)
            {
                grid.HonoreeId = honoree.Id;
            }

            if (!active && reason is not null)
            {
                db.FlagGridRemovalAudits.Add(new FlagGridRemovalAudit
                {
                    HonoreeId = honoree.Id,
                    FlagGridId = gridId,
                    Action = "Remove",
                    ActorEmail = who ?? "remover@example.test",
                    OccurredUtc = now,
                    Reason = reason,
                    BeforeState = "{}",
                    AfterState = "{}"
                });
                if (openRemoval)
                {
                    db.FlagRemovalQueueItems.Add(new FlagRemovalQueueItem
                    {
                        HonoreeId = honoree.Id,
                        HonoreeName = $"{first} {last}",
                        FlagGridId = gridId,
                        FlagGridName = grid.FlagGridName,
                        Source = FlagRemovalQueue.Manual,
                        SourceDetail = reason,
                        CreatedUtc = now,
                        CreatedBy = who ?? "remover@example.test"
                    });
                }
            }

            await db.SaveChangesAsync();
        }

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

    private sealed record ListPayload(List<ListItem> Items, int Page, int PageSize, int Total);

    private sealed record ListItem(
        int HonoreeId,
        string Name,
        string? Branch,
        string? FormerFlagGridName,
        DateTime? DeactivatedUtc,
        string DeactivatedBy,
        string Reason,
        string SlotStatus,
        bool CanReactivate);

    private sealed record ReactivatePayload(List<ReactivateItem> Results);

    private sealed record ReactivateItem(
        int HonoreeId,
        string Outcome,
        string Code,
        string Message,
        bool ReprintQueued,
        bool RemovalCanceled);
}
