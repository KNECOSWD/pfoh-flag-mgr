using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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

public class RetryIdempotencyTests
{
    [Fact]
    public async Task A_transient_failure_on_the_first_save_inserts_one_row()
    {
        await using var factory = new RetryingClaimFactory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            db.FlagGrids.Add(new FlagGrid
            {
                Id = 4,
                FlagGridName = "A-04",
                Notes = string.Empty,
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/flag-claims/4/claim");
        request.Headers.Add(TestAuthHandler.OidHeader, "claim-user");
        request.Headers.Add(TestAuthHandler.EmailHeader, "claim@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Claim Person");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        await using var check = factory.Services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(1, await saved.FlagClaims.CountAsync());
        Assert.Equal(1, factory.TransientSaves);
    }

    private sealed class RetryingClaimFactory : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"retry-claim-{Guid.NewGuid():N}";
        private readonly TransientFlagClaimSaveInterceptor interceptor = new();

        public int TransientSaves => interceptor.Failures;

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

                services.AddSingleton(interceptor);
                services.AddDbContext<PfohDbContext>((sp, options) => options
                    .UseInMemoryDatabase(databaseName)
                    .ReplaceService<IExecutionStrategyFactory, TransientRetryStrategyFactory>()
                    .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                    .AddInterceptors(
                        sp.GetRequiredService<PublicFlagMapCacheInvalidator>(),
                        interceptor));

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
    }

    private sealed class TransientFlagClaimSaveInterceptor : SaveChangesInterceptor
    {
        private int failures;

        public int Failures => failures;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowOnce(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnce(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void ThrowOnce(DbContext? context)
        {
            if (context is null || failures > 0)
            {
                return;
            }

            var addingClaim = context.ChangeTracker.Entries<FlagClaim>()
                .Any(entry => entry.State == EntityState.Added);
            if (!addingClaim)
            {
                return;
            }

            failures++;
            throw new TimeoutException("Synthetic transient save failure.");
        }
    }

    private sealed class TransientRetryStrategyFactory : IExecutionStrategyFactory
    {
        private readonly ExecutionStrategyDependencies dependencies;

        public TransientRetryStrategyFactory(ExecutionStrategyDependencies dependencies)
        {
            this.dependencies = dependencies;
        }

        public IExecutionStrategy Create() => new TransientRetryStrategy(dependencies);
    }

    private sealed class TransientRetryStrategy : ExecutionStrategy
    {
        public TransientRetryStrategy(ExecutionStrategyDependencies dependencies)
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
