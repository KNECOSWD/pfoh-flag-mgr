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
/// transactions, so two overlapping restores cannot contend.
/// SQLite is already used by the merge and profile race tests. It has a real
/// write lock and a busy timeout, so two HTTP restores of one honoree run
/// at the same time and only one approval-queue row is saved.
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
    public async Task Two_concurrent_reactivations_of_one_honoree_write_one_audit()
    {
        await ResetAsync();
        var honoreeId = await SeedInactiveAsync("Ada", "Lovelace");

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
            var responses = await Task.WhenAll(firstClient.SendAsync(Admin(honoreeId)), secondClient.SendAsync(Admin(honoreeId)));
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
        var honoree = await database.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.False(honoree.IsActive);
        Assert.Null(honoree.FlagGridId);
        Assert.Equal(1, await database.HonoreeChangeRequests.CountAsync(row => row.HonoreeId == honoreeId && row.RequestStatus == "Submitted" && row.FlagGridId == null));
        Assert.Equal(1, await database.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore" && row.FlagGridId == null && row.Reason == InactiveHonoreeAdminService.ApprovalQueueNote));

        HttpRequestMessage Admin(int id)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/inactive-honorees/reactivate")
            {
                Content = JsonContent.Create(new { items = new[] { new { honoreeId = id } } })
            };
            request.Headers.Add(TestAuthHandler.OidHeader, "admin-1");
            request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.test");
            request.Headers.Add(TestAuthHandler.NameHeader, "Admin");
            request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
            return request;
        }
    }

    [Fact]
    public async Task Two_concurrent_review_queue_adds_write_one_audit()
    {
        await ResetAsync();
        var honoreeId = await SeedInactiveAsync("Ada", "Lovelace");

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
            var responses = await Task.WhenAll(
                firstClient.SendAsync(Queue(honoreeId)),
                secondClient.SendAsync(Queue(honoreeId)));
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
        var honoree = await database.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.False(honoree.IsActive);
        Assert.Null(honoree.FlagGridId);
        Assert.Equal(1, await database.HonoreeChangeRequests.CountAsync(row => row.HonoreeId == honoreeId && row.RequestStatus == "Submitted" && row.FlagGridId == null));
        Assert.Equal(1, await database.FlagGridRemovalAudits.CountAsync(row => row.Action == "Restore" && row.FlagGridId == null && row.Reason == InactiveHonoreeAdminService.ReactivationPageQueueNote));
        Assert.Equal(0, await database.HonoreeReactivationTokens.CountAsync());
        Assert.Equal(0, await database.HonoreeReactivationRequests.CountAsync());

        HttpRequestMessage Queue(int id)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/reactivation/review-queue")
            {
                Content = JsonContent.Create(new { items = new[] { new { honoreeId = id } } })
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
        db.HonoreeReactivationRequests.RemoveRange(db.HonoreeReactivationRequests);
        db.HonoreeReactivationTokens.RemoveRange(db.HonoreeReactivationTokens);
        db.ReprintQueueEvents.RemoveRange(db.ReprintQueueEvents);
        db.FlagGridRemovalAudits.RemoveRange(db.FlagGridRemovalAudits);
        db.FlagRemovalQueueItems.RemoveRange(db.FlagRemovalQueueItems);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedInactiveAsync(string first, string last)
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
            FlagGridId = null,
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
