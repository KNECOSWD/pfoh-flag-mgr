using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

/// <summary>
/// The shared API fixture uses EF Core's in-memory provider, which ignores
/// transactions, so two overlapping restores cannot contend for a row.
/// SQLite is already used by the merge and profile race tests. It has a real
/// write lock and a busy timeout, so two HTTP restores of one free flag run
/// at the same time and the database lets only one commit.
/// </summary>
public sealed class InactiveHonoreeSqliteFactory : PfohApiFactory
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"pfoh-inactive-race-{Guid.NewGuid():N}.db");

    public InactiveHonoreeSqliteFactory()
    {
        SqliteConnectionString = $"Data Source={path};Default Timeout=30;Cache=Shared";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The pooled connection can outlive the host for a moment.
        }
    }
}

public class InactiveHonoreeRaceTests(InactiveHonoreeSqliteFactory factory) : IClassFixture<InactiveHonoreeSqliteFactory>
{
    [Fact]
    public async Task Two_concurrent_restores_of_one_free_flag_save_only_one_honoree()
    {
        await ResetAsync();
        var gridId = await SeedGridAsync("Q-40");
        var first = await SeedInactiveAsync("Ada", "Lovelace", gridId);
        var second = await SeedInactiveAsync("Grace", "Hopper", gridId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS UX_Honorees_OneActiveFlagGrid
                ON Honorees (FlagGridId)
                WHERE IsActive = 1 AND DeletedDate IS NULL AND FlagGridId IS NOT NULL;
                """);
        }

        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = factory.Services.GetRequiredService<InactiveRestoreGate>();
        gate.BeforeRestore = async ct =>
        {
            if (Interlocked.Increment(ref entered) == 2)
            {
                bothEntered.TrySetResult();
            }

            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        };

        try
        {
            using var firstClient = factory.CreateClient();
            using var secondClient = factory.CreateClient();
            var firstTask = firstClient.SendAsync(Admin(first));
            var secondTask = secondClient.SendAsync(Admin(second));
            var responses = await Task.WhenAll(firstTask, secondTask);
            var statuses = responses.Select(response => response.StatusCode).OrderBy(status => status).ToArray();
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
            Assert.Equal(2, entered);
        }
        finally
        {
            gate.BeforeRestore = null;
        }

        await using var after = factory.Services.CreateAsyncScope();
        var database = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        var active = await database.Honorees.Where(honoree => honoree.IsActive && honoree.FlagGridId == gridId).ToListAsync();
        Assert.Single(active);
        var otherId = active[0].Id == first ? second : first;
        Assert.False((await database.Honorees.SingleAsync(honoree => honoree.Id == otherId)).IsActive);
        Assert.Equal(gridId, (await database.FlagGrids.SingleAsync(grid => grid.Id == gridId)).HonoreeId);
        Assert.Equal(1, await database.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore"));

        HttpRequestMessage Admin(int honoreeId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate")
            {
                Content = JsonContent.Create(new { items = new[] { new { honoreeId, flagGridId = gridId } } })
            };
            request.Headers.Add(TestAuthHandler.OidHeader, "admin-1");
            request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.test");
            request.Headers.Add(TestAuthHandler.NameHeader, "Admin");
            request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
            return request;
        }
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.ReprintQueueEvents.RemoveRange(db.ReprintQueueEvents);
        db.FlagGridRemovalAudits.RemoveRange(db.FlagGridRemovalAudits);
        db.FlagRemovalQueueItems.RemoveRange(db.FlagRemovalQueueItems);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        await db.SaveChangesAsync();
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

    private async Task<int> SeedInactiveAsync(string first, string last, int gridId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        var honoree = new Honoree
        {
            FirstName = first,
            LastName = last,
            Rank = "Sergeant",
            Description = "A clearly fake tribute.",
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = string.Empty,
            PhoneNumber = string.Empty,
            EmailAddress = string.Empty,
            Awards = string.Empty,
            ConflictsServed = string.Empty,
            DatesUserEntry = string.Empty,
            IsActive = false,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.Honorees.Add(honoree);
        await db.SaveChangesAsync();
        db.FlagGridRemovalAudits.Add(new FlagGridRemovalAudit
        {
            HonoreeId = honoree.Id,
            FlagGridId = gridId,
            Action = "Remove",
            ActorEmail = "remover@example.test",
            OccurredUtc = now,
            Reason = "Moved away",
            BeforeState = "{}",
            AfterState = "{}"
        });
        await db.SaveChangesAsync();
        return honoree.Id;
    }
}
