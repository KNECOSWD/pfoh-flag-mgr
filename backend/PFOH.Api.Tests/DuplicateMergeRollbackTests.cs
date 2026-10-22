using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

// Startup runs UserProfileSchema.EnsureAsync against SQLite and sets the
// process-wide ready flag. Keep this class with the other schema tests so
// that flag cannot flip while a profile-store retry test is in progress.
[Collection("UserProfileSchema")]
public class DuplicateMergeRollbackTests : IClassFixture<DuplicateMergeRollbackFactory>
{
    private readonly DuplicateMergeRollbackFactory factory;

    public DuplicateMergeRollbackTests(DuplicateMergeRollbackFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task A_failed_record_count_check_leaves_the_merge_unsaved()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
        }

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/duplicates/merge")
        {
            Content = JsonContent.Create(new
            {
                survivorId = 2,
                duplicateIds = new[] { 1, 2 },
                fields = new Dictionary<string, int>
                {
                    ["firstName"] = 2,
                    ["middleName"] = 2,
                    ["lastName"] = 2,
                    ["suffix"] = 2,
                    ["nickname"] = 2,
                    ["rank"] = 2,
                    ["serviceBranch"] = 2,
                    ["serviceYears"] = 2,
                    ["conflictsServed"] = 2,
                    ["awards"] = 2,
                    ["description"] = 1,
                    ["kia"] = 2,
                    ["photo"] = 1,
                    ["flagGrid"] = 2,
                    ["phone"] = 2,
                    ["email"] = 2,
                    ["sponsor"] = 2
                }
            })
        };
        request.Headers.Add(TestAuthHandler.OidHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Admin");
        request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, body);
        Assert.Contains("would have created or removed a record", body);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honorees = await after.Honorees.OrderBy(honoree => honoree.Id).ToListAsync();
        Assert.Equal(2, honorees.Count);
        Assert.Equal("Photo bio", honorees[0].Description);
        Assert.Equal("photo-from-1.jpg", honorees[0].PhotoFileName);
        Assert.True(honorees[0].IsActive);
        Assert.Equal(11, honorees[0].FlagGridId);
        Assert.Equal("Kept bio", honorees[1].Description);
        Assert.Equal("photo-from-2.jpg", honorees[1].PhotoFileName);
        Assert.True(honorees[1].IsActive);
        Assert.Equal(12, honorees[1].FlagGridId);

        var grids = await after.FlagGrids.OrderBy(grid => grid.Id).ToListAsync();
        Assert.Equal(1, grids[0].HonoreeId);
        Assert.Equal(2, grids[1].HonoreeId);

        var claims = await after.FlagClaims.OrderBy(claim => claim.Id).ToListAsync();
        Assert.Equal(2, claims.Count);
        Assert.Equal(1, claims[0].HonoreeId);
        Assert.Equal(11, claims[0].FlagGridId);
        Assert.Equal(2, claims[1].HonoreeId);
        Assert.Equal(12, claims[1].FlagGridId);

        var ownership = await after.HonoreeClaims.OrderBy(claim => claim.HonoreeId).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, ownership.Select(claim => claim.HonoreeId).ToArray());
        Assert.Equal(new[] { "person-a", "person-b" }, ownership.Select(claim => claim.UserId).ToArray());
        Assert.All(ownership, claim => Assert.Equal("Claimed", claim.ClaimStatus));
    }

    private static async Task SeedAsync(PfohDbContext db)
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
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
            ServiceBranchName = "United States Army",
            Description = "Army",
            LogoFileName = "army.png",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.FlagGrids.Add(Grid(11, "AA-1", now));
        db.FlagGrids.Add(Grid(12, "AA-2", now));
        db.Honorees.Add(Honoree(1, "Photo bio", "photo-from-1.jpg", 11, now));
        db.Honorees.Add(Honoree(2, "Kept bio", "photo-from-2.jpg", 12, now));
        await db.SaveChangesAsync();
        db.FlagGrids.Single(grid => grid.Id == 11).HonoreeId = 1;
        db.FlagGrids.Single(grid => grid.Id == 12).HonoreeId = 2;
        db.FlagClaims.Add(Claim(31, 1, 11, "person-a", "ada@example.com", "Ada", now));
        db.FlagClaims.Add(Claim(32, 2, 12, "person-b", "bea@example.com", "Bea", now));
        db.HonoreeClaims.Add(Ownership(1, 31, "person-a", "ada@example.com", "Ada", now));
        db.HonoreeClaims.Add(Ownership(2, 32, "person-b", "bea@example.com", "Bea", now));
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

    private static Honoree Honoree(int id, string description, string photo, int gridId, DateTime now) => new()
    {
        Id = id,
        FirstName = "John",
        LastName = "Smith",
        Rank = "Private",
        Description = description,
        PhotoFileName = photo,
        FlagGridId = gridId,
        ServiceBranchId = 1,
        ServiceBranchCategoryId = 1,
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

    private static FlagClaim Claim(int id, int honoreeId, int gridId, string userId, string email, string name, DateTime now) => new()
    {
        Id = id,
        HonoreeId = honoreeId,
        FlagGridId = gridId,
        ExternalUserObjectId = userId,
        ExternalUserEmail = email,
        ExternalUserName = name,
        ClaimStatus = "Claimed",
        CreatedUtc = now
    };

    private static HonoreeClaim Ownership(int honoreeId, int sourceId, string userId, string email, string name, DateTime now) => new()
    {
        HonoreeId = honoreeId,
        UserId = userId,
        Email = email,
        DisplayName = name,
        ClaimStatus = "Claimed",
        ClaimedUtc = now,
        SourceFlagClaimId = sourceId
    };
}

public sealed class DuplicateMergeRollbackFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("Data Source=merge-rollback;Mode=Memory;Cache=Shared");

    public DuplicateMergeRollbackFactory()
    {
        connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:AuthenticatedGetPermit"] = "100000"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            var descriptors = services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(DbContextOptions<PfohDbContext>) ||
                    descriptor.ServiceType == typeof(PfohDbContext))
                .ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<MergeCountFaultInterceptor>();
            services.AddDbContext<PfohDbContext>((sp, options) => options
                .UseSqlite(connection)
                .AddInterceptors(sp.GetRequiredService<MergeCountFaultInterceptor>()));

            services.RemoveAll<HonoreeFileStorage>();
            services.AddSingleton<HonoreeFileStorage, PfohApiFactory.PendingReviewTestFileStorage>();

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            connection.Dispose();
        }
    }
}

/// <summary>
/// Inserts one extra honoree while the merge is saving, so the record-count
/// check fails. The insert is part of that same save, inside the merge transaction.
/// </summary>
public sealed class MergeCountFaultInterceptor : SaveChangesInterceptor
{
    private bool fired;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var db = eventData.Context;
        if (db is not null && !fired && db.ChangeTracker.Entries<Honoree>().Any(entry =>
                entry.State == EntityState.Modified && !entry.Entity.IsActive))
        {
            fired = true;
            db.Set<Honoree>().Add(new Honoree
            {
                FirstName = "Extra",
                LastName = "Row",
                Rank = string.Empty,
                Description = string.Empty,
                PhotoFileName = string.Empty,
                Salutation = string.Empty,
                NameUserEntry = string.Empty,
                PhoneNumber = string.Empty,
                EmailAddress = string.Empty,
                Awards = string.Empty,
                ConflictsServed = string.Empty,
                DatesUserEntry = string.Empty,
                IsActive = true,
                CreatedBy = "fault",
                ModifiedBy = "fault",
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow
            });
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
