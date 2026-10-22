using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PFOH.Api.Data;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

[Collection("UserProfileSchema")]
public class ProfileSqlRetryTests
{
    [Fact]
    public async Task SqlServer_retries_a_transient_error_three_times_then_stops()
    {
        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseSqlServer("Server=127.0.0.1,1433;Database=unused;User ID=unused;Password=unused;Encrypt=False")
            .Options;
        await using var db = new PfohDbContext(options);
        var strategy = new SqlServerRetryingExecutionStrategy(
            db,
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromMilliseconds(10),
            errorNumbersToAdd: null);
        var attempts = 0;
        var error = await Assert.ThrowsAsync<RetryLimitExceededException>(() =>
            strategy.ExecuteAsync<int>(() =>
            {
                attempts++;
                return Task.FromException<int>(SqlError(1205, "deadlock for oid-strategy-secret"));
            }));

        Assert.Equal(4, attempts);
        Assert.IsType<SqlException>(error.InnerException);
        var program = File.ReadAllText(RepoFile("backend", "PFOH.Api", "Program.cs"));
        Assert.Contains("maxRetryCount: ProfileSqlRetry.MaxRetryCount", program, StringComparison.Ordinal);
        Assert.Contains("maxRetryDelay: ProfileSqlRetry.MaxRetryDelay", program, StringComparison.Ordinal);
        Assert.Contains("EnableRetryOnFailure", program, StringComparison.Ordinal);
        Assert.Equal(3, ProfileSqlRetry.MaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(1), ProfileSqlRetry.MaxRetryDelay);
    }

    [Fact]
    public async Task Retry_delays_match_ef_for_a_throttling_error_and_a_deadlock()
    {
        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseSqlServer("Server=127.0.0.1,1433;Database=unused;User ID=unused;Password=unused;Encrypt=False;Connect Timeout=1")
            .Options;
        await using var db = new PfohDbContext(options);
        var throttled = new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(6)
        };
        var deadlock = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)
        };

        for (var sample = 0; sample < 20; sample++)
        {
            var throttledDelays = await MeasureDelays(db, SqlError(40613, "database unavailable"));
            var deadlockDelays = await MeasureDelays(db, SqlError(1205, "deadlock"));
            Assert.Equal(throttled, throttledDelays);
            Assert.Equal(deadlock, deadlockDelays);
        }

        var total = throttled.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay);
        Assert.Equal(TimeSpan.FromSeconds(17), total);
        Assert.True(total < ProfileSqlRetry.TotalWaitBudget);
    }

    [Fact]
    public async Task Schema_step_retries_in_a_single_layer()
    {
        UserProfileSchema.ResetForTests();
        ProfileStoreResilience.PipelineForTests.Value = ProfileStoreResilience.ImmediateRetries();
        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1433;Database=unused;User ID=unused;Password=unused;Encrypt=False;Connect Timeout=1",
                sql => sql.EnableRetryOnFailure(
                    maxRetryCount: ProfileSqlRetry.MaxRetryCount,
                    maxRetryDelay: TimeSpan.FromMilliseconds(1),
                    errorNumbersToAdd: null))
            .Options;
        await using var db = new PfohDbContext(options);
        var attempts = 0;
        UserProfileSchema.EnsureFailureForTests.Value = _ =>
        {
            attempts++;
            throw SqlError(1205, "deadlock");
        };
        var circuit = new ProfileStoreCircuit();
        try
        {
            await Assert.ThrowsAsync<RetryLimitExceededException>(() =>
                UserProfileSchema.EnsureAsync(
                    db,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    CancellationToken.None,
                    circuit));
            var limit = ProfileSqlRetry.MaxRetryCount + 1;
            Assert.Equal(limit, attempts);
            Assert.True(attempts <= limit);
            Assert.Equal(ProfileStoreCircuitState.Open, circuit.State);
        }
        finally
        {
            UserProfileSchema.EnsureFailureForTests.Value = null;
            ProfileStoreResilience.PipelineForTests.Value = null;
            UserProfileSchema.ResetForTests();
        }
    }

    [Fact]
    public void Startup_with_the_database_down_stays_well_under_230_seconds()
    {
        var worst = StartupDatabase.WorstCaseWhenDatabaseIsDown;
        Assert.Equal(9, StartupDatabase.StepCount);
        Assert.Equal(TimeSpan.FromSeconds(5), StartupDatabase.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), worst);
        Assert.True(worst < TimeSpan.FromSeconds(120));
        Assert.True(worst < TimeSpan.FromSeconds(230));

        using var db = StartupDatabase.Create(
            "Server=127.0.0.1,1433;Database=unused;User ID=unused;Password=unused;Encrypt=False");
        Assert.False(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        var program = File.ReadAllText(RepoFile("backend", "PFOH.Api", "Program.cs"));
        Assert.Contains("StartupDatabase.Create", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_read_returns_503_when_sql_retries_are_exhausted()
    {
        const string secret = "oid-sql-retry-secret";
        UserProfileSchema.ResetForTests();
        await using var factory = new RetryExhaustedFactory(secret);
        try
        {
            var client = factory.CreateClient();
            var live = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("Healthy", (await live.Content.ReadAsStringAsync()).Trim());

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
            request.Headers.Add(TestAuthHandler.OidHeader, "retry-user");
            request.Headers.Add(TestAuthHandler.EmailHeader, "retry@example.com");
            request.Headers.Add(TestAuthHandler.NameHeader, "Retry Person");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var profile = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, profile.StatusCode);
            Assert.True(profile.Headers.TryGetValues("Retry-After", out var retryAfter));
            Assert.Equal("5", Assert.Single(retryAfter));
            var body = await profile.Content.ReadAsStringAsync();
            Assert.Contains("The profile store is unavailable.", body, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        }
        finally
        {
            UserProfileSchema.ResetForTests();
        }
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static SqlException SqlError(int number, string message)
    {
        var assembly = typeof(SqlException).Assembly;
        var errorType = assembly.GetType("Microsoft.Data.SqlClient.SqlError", throwOnError: true)!;
        var error = Activator.CreateInstance(
            errorType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { number, (byte)0, (byte)14, "server", message, "proc", 1, (uint)0, null },
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
        return (SqlException)ctor!.Invoke(new object?[] { message, collection, null, Guid.Empty });
    }

    private static async Task<List<TimeSpan>> MeasureDelays(PfohDbContext db, Exception failure)
    {
        var delays = new List<TimeSpan>();
        var strategy = new RecordingSqlRetryStrategy(
            db,
            ProfileSqlRetry.MaxRetryCount,
            ProfileSqlRetry.MaxRetryDelay,
            delays);
        await Assert.ThrowsAsync<RetryLimitExceededException>(() =>
            strategy.ExecuteAsync<int>(() => Task.FromException<int>(failure)));
        return delays;
    }

    private sealed class RecordingSqlRetryStrategy : SqlServerRetryingExecutionStrategy
    {
        private readonly List<TimeSpan> delays;

        public RecordingSqlRetryStrategy(
            DbContext context,
            int maxRetryCount,
            TimeSpan maxRetryDelay,
            List<TimeSpan> delays)
            : base(context, maxRetryCount, maxRetryDelay, errorNumbersToAdd: null)
        {
            this.delays = delays;
        }

        protected override TimeSpan? GetNextDelay(Exception lastException)
        {
            var delay = base.GetNextDelay(lastException);
            if (delay is null)
            {
                return null;
            }

            delays.Add(delay.Value);
            return TimeSpan.Zero;
        }
    }

    private sealed class RetryExhaustedFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("Data Source=sql-retry;Mode=Memory;Cache=Shared");
        private readonly string secret;

        public RetryExhaustedFactory(string secret)
        {
            this.secret = secret;
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

                services.AddSingleton(new ExhaustProfileReadInterceptor(secret));
                services.AddDbContext<PfohDbContext>((sp, options) => options
                    .UseSqlite(connection)
                    .AddInterceptors(sp.GetRequiredService<ExhaustProfileReadInterceptor>()));
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

    private sealed class ExhaustProfileReadInterceptor(string secret) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfProfileRead(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfProfileRead(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowIfProfileRead(string commandText)
        {
            if (commandText.Contains("FROM \"UserProfiles\"", StringComparison.Ordinal)
                || commandText.Contains("FROM UserProfiles", StringComparison.Ordinal))
            {
                throw new RetryLimitExceededException(
                    $"retries exhausted for {secret}",
                    SqlError(1205, $"deadlock for {secret}"));
            }
        }
    }
}
