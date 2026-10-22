using System.Security.Claims;
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
    public void Unique_violation_messages_are_recognized_without_reading_row_text()
    {
        var exception = new DbUpdateException("save failed", new InvalidOperationException("UNIQUE constraint failed: UserProfiles.OwnerObjectId"));
        Assert.True(UserProfileService.IsUniqueViolation(exception));
        Assert.False(UserProfileService.IsUniqueViolation(new DbUpdateException("timeout", new TimeoutException("connection"))));
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
                    new InvalidOperationException("UNIQUE constraint failed: UserProfiles.OwnerObjectId"));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
