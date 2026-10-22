using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

/// <summary>
/// A transient fault after the database commit must not run the write again.
/// The interceptor throws from TransactionCommitted, which EF calls only after
/// the database commit has returned.
/// </summary>
[Collection("UserProfileSchema")]
public class PostCommitFaultTests
{
    [Fact]
    public async Task A_fault_after_commit_writes_one_merge_audit()
    {
        var interceptor = new AfterCommitFaultInterceptor();
        await using var factory = new PostCommitFactory(interceptor);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            await db.Database.EnsureCreatedAsync();
            await SeedMergeAsync(db);
        }

        interceptor.Arm();
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
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal(1, interceptor.Faults);

        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var audits = await saved.HonoreeMergeAudits.ToListAsync();
        var audit = Assert.Single(audits);
        Assert.NotNull(audit.ClientRequestId);
    }

    [Fact]
    public async Task A_fault_after_commit_writes_one_nomination()
    {
        var interceptor = new AfterCommitFaultInterceptor();
        await using var factory = new PostCommitFactory(interceptor);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            await db.Database.EnsureCreatedAsync();
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
                ServiceBranchName = "United States Army",
                Description = "Army",
                LogoFileName = "army.png",
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now
            });
            await db.SaveChangesAsync();
        }

        interceptor.Arm();
        var client = factory.CreateClient();
        var content = new MultipartFormDataContent
        {
            { new StringContent("Test"), "FirstName" },
            { new StringContent("Nominee"), "LastName" },
            { new StringContent("1"), "ServiceBranchId" },
            { new StringContent("1"), "ServiceBranchCategoryId" },
            { new StringContent("A tribute for the honoree"), "Description" },
            { new StringContent("2145550100"), "SubmitterPhoneNumber" },
            { new StringContent("submitter@example.com"), "SubmitterEmailAddress" },
            { new StringContent("false"), "Kia" }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/flag-claims/nominate")
        {
            Content = content
        };
        request.Headers.Add(TestAuthHandler.OidHeader, "submitter");
        request.Headers.Add(TestAuthHandler.EmailHeader, "submitter@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Test Submitter");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal(1, interceptor.Faults);

        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var claim = Assert.Single(await saved.FlagClaims.ToListAsync());
        Assert.NotNull(claim.ClientRequestId);
        var change = Assert.Single(await saved.HonoreeChangeRequests.ToListAsync());
        Assert.Equal(claim.Id, change.FlagClaimId);
    }

    private static async Task SeedMergeAsync(PfohDbContext db)
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
        db.Honorees.Add(Person(1, "Photo bio", "photo-from-1.jpg", 11, now));
        db.Honorees.Add(Person(2, "Kept bio", "photo-from-2.jpg", 12, now));
        await db.SaveChangesAsync();
        db.FlagGrids.Single(grid => grid.Id == 11).HonoreeId = 1;
        db.FlagGrids.Single(grid => grid.Id == 12).HonoreeId = 2;
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

    private static Honoree Person(int id, string description, string photo, int gridId, DateTime now) => new()
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

    private sealed class PostCommitFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection;
        private readonly AfterCommitFaultInterceptor interceptor;

        public PostCommitFactory(AfterCommitFaultInterceptor interceptor)
        {
            connection = new SqliteConnection($"Data Source=post-commit-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            connection.Open();
            this.interceptor = interceptor;
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

                var fault = interceptor;
                services.AddSingleton(fault);
                services.AddDbContext<PfohDbContext>(options => options
                    .UseSqlite(connection)
                    .ReplaceService<IExecutionStrategyFactory, PostCommitRetryStrategyFactory>()
                    .AddInterceptors(fault));

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

    private sealed class AfterCommitFaultInterceptor : DbTransactionInterceptor
    {
        private int armed;
        private int faults;

        public int Faults => faults;

        public void Arm() => armed = 1;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (armed == 1 && eventData.Context is not null && TracksIdempotentWrite(eventData.Context))
            {
                if (Interlocked.Increment(ref faults) == 1)
                {
                    throw new TimeoutException("Synthetic fault after commit.");
                }
            }

            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }

        private static bool TracksIdempotentWrite(DbContext context) =>
            context.ChangeTracker.Entries<FlagClaim>().Any(entry => entry.Entity.ClientRequestId != null)
            || context.ChangeTracker.Entries<HonoreeMergeAudit>().Any(entry => entry.Entity.ClientRequestId != null);
    }

    private sealed class PostCommitRetryStrategyFactory : IExecutionStrategyFactory
    {
        private readonly ExecutionStrategyDependencies dependencies;

        public PostCommitRetryStrategyFactory(ExecutionStrategyDependencies dependencies)
        {
            this.dependencies = dependencies;
        }

        public IExecutionStrategy Create() => new PostCommitRetryStrategy(dependencies);
    }

    private sealed class PostCommitRetryStrategy : ExecutionStrategy
    {
        public PostCommitRetryStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
        {
        }

        protected override bool ShouldRetryOn(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is TimeoutException)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
