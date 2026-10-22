using System.Reflection;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class UserProfileProvisioningRaceTests
{
    [Fact]
    public async Task A_unique_violation_on_the_first_insert_rereads_the_one_row()
    {
        var databaseName = $"provision-race-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using (var setup = new PfohDbContext(options))
        {
            setup.Database.EnsureCreated();
        }

        var raced = new DbContextOptionsBuilder<PfohDbContext>()
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(new InsertAheadInterceptor(options, "oid-race"))
            .Options;
        await using var db = new PfohDbContext(raced);
        var service = new UserProfileService(db, NullLogger<UserProfileService>.Instance, new ConfigurationBuilder().Build());
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", "oid-race"),
            new Claim("preferred_username", "race@example.com"),
            new Claim("name", "Race Person")
        ], "Test"));

        await service.EnsureProvisionedAsync(user, CancellationToken.None);

        await using var check = new PfohDbContext(options);
        var rows = await check.UserProfiles.Where(profile => profile.OwnerObjectId == "oid-race").ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("race@example.com", row.Email);
        Assert.Equal(ProfileFieldSource.Claim, row.DisplayNameSource);
    }

    [Fact]
    public void Unique_violations_are_recognized_by_provider_error_code()
    {
        var sqlite = new DbUpdateException(
            "save failed",
            new SqliteException("UNIQUE constraint failed: UserProfiles.OwnerObjectId", 19));
        Assert.True(UserProfileService.IsUniqueViolation(sqlite));
        Assert.True(UserProfileService.IsUniqueViolation(new DbUpdateException(
            "save failed",
            new SqliteException("UNIQUE constraint failed: UserProfiles.OwnerObjectId", 2067))));
        Assert.True(UserProfileService.IsUniqueViolation(new DbUpdateException("save failed", SqlError(2601))));
        Assert.True(UserProfileService.IsUniqueViolation(new DbUpdateException("save failed", SqlError(2627))));

        Assert.False(UserProfileService.IsUniqueViolation(new DbUpdateException(
            "save failed",
            new InvalidOperationException("UNIQUE constraint failed: UserProfiles.OwnerObjectId duplicate 2601 2627"))));
        Assert.False(UserProfileService.IsUniqueViolation(new DbUpdateException("save failed", SqlError(547))));
        Assert.False(UserProfileService.IsUniqueViolation(new DbUpdateException(
            "save failed",
            new SqliteException("disk I/O error", 10))));
        Assert.False(UserProfileService.IsUniqueViolation(new DbUpdateException("timeout", new TimeoutException("connection"))));
    }

    [Fact]
    public void Provision_gates_stay_inside_a_fixed_stripe_set()
    {
        Assert.Equal(32, UserProfileService.ProvisionGateCount);
        Assert.Same(UserProfileService.ProvisionGateFor("oid-a"), UserProfileService.ProvisionGateFor("oid-a"));
        var seen = new HashSet<SemaphoreSlim>();
        for (var i = 0; i < 500; i++)
        {
            seen.Add(UserProfileService.ProvisionGateFor($"oid-{i}"));
        }

        Assert.InRange(seen.Count, 1, UserProfileService.ProvisionGateCount);
    }

    private static SqlException SqlError(int number)
    {
        var assembly = typeof(SqlException).Assembly;
        var errorType = assembly.GetType("Microsoft.Data.SqlClient.SqlError", throwOnError: true)!;
        var error = Activator.CreateInstance(
            errorType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { number, (byte)0, (byte)14, "server", "provider error", "proc", 1, (uint)0, null },
            culture: null);
        var collectionType = assembly.GetType("Microsoft.Data.SqlClient.SqlErrorCollection", throwOnError: true)!;
        var collection = Activator.CreateInstance(
            collectionType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: Array.Empty<object>(),
            culture: null);
        collectionType.GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(collection, new[] { error });
        var ctor = typeof(SqlException).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(string), collectionType, typeof(Exception), typeof(Guid) },
            modifiers: null);
        return (SqlException)ctor!.Invoke(new object?[] { "provider error", collection, null, Guid.Empty });
    }

    private sealed class InsertAheadInterceptor(DbContextOptions<PfohDbContext> options, string ownerObjectId) : SaveChangesInterceptor
    {
        private int hits;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref hits) == 1)
            {
                using var other = new PfohDbContext(options);
                other.UserProfiles.Add(new UserProfile
                {
                    OwnerObjectId = ownerObjectId,
                    DisplayName = "Existing",
                    Email = "existing@example.com",
                    Phone = string.Empty,
                    DisplayNameSource = ProfileFieldSource.Claim,
                    PhoneSource = ProfileFieldSource.Claim,
                    OrganizationSource = ProfileFieldSource.Claim,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
                other.SaveChanges();
                throw new DbUpdateException(
                    "save failed",
                    new SqliteException("UNIQUE constraint failed: UserProfiles.OwnerObjectId", 19));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
