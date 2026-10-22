using System.Reflection;
using System.Security.Claims;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PFOH.Api.Controllers;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

[Collection("UserProfileSchema")]
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

    [Fact]
    public async Task Profile_write_failures_log_the_exception_type_and_sql_number_not_the_oid()
    {
        const string oid = "oid-log-secret-9f3a";
        var logs = new ListLogger();
        var updateDatabase = $"profile-log-update-{Guid.NewGuid():N}";
        var updateOptions = new DbContextOptionsBuilder<PfohDbContext>()
            .UseInMemoryDatabase(updateDatabase)
            .AddInterceptors(new FailFirstSaveInterceptor(oid))
            .Options;
        await using (var setup = new PfohDbContext(new DbContextOptionsBuilder<PfohDbContext>().UseInMemoryDatabase(updateDatabase).Options))
        {
            setup.Database.EnsureCreated();
            setup.UserProfiles.Add(ProfileRow(oid, "2145551234", "Kept Org"));
            setup.SaveChanges();
        }

        await using (var db = new PfohDbContext(updateOptions))
        {
            var service = new UserProfileService(db, logs, new ConfigurationBuilder().Build());
            var user = ProfileUser(oid, "ada@example.com", "Ada Person");
            var result = await service.UpdateOwnAsync(user, oid, oid, "Ada Updated", "214-555-1234", "Kept Org", CancellationToken.None);
            Assert.Null(result.Error);
            Assert.NotNull(result.Profile);
        }

        var readDatabase = $"profile-log-read-{Guid.NewGuid():N}";
        var readOptions = new DbContextOptionsBuilder<PfohDbContext>()
            .UseInMemoryDatabase(readDatabase)
            .AddInterceptors(new FailSecondSaveInterceptor(oid))
            .Options;
        await using (var setup = new PfohDbContext(new DbContextOptionsBuilder<PfohDbContext>().UseInMemoryDatabase(readDatabase).Options))
        {
            setup.Database.EnsureCreated();
            setup.UserProfiles.Add(ProfileRow(oid, string.Empty, "Kept Org"));
            setup.SaveChanges();
        }

        await using (var db = new PfohDbContext(readOptions))
        {
            var service = new UserProfileService(db, logs, new ConfigurationBuilder().Build());
            var user = ProfileUser(oid, "ada@example.com", "Ada Person", phone: "214-555-0199");
            var profile = await service.GetOwnAsync(user, CancellationToken.None);
            Assert.Equal("Ada Person", profile.DisplayName);
        }

        var written = string.Join("\n", logs.Lines);
        Assert.Contains("Retrying profile save after a write conflict.", written, StringComparison.Ordinal);
        Assert.Contains("Phone or organization from the sign-in token could not be saved.", written, StringComparison.Ordinal);
        Assert.Contains("ExceptionType=Microsoft.EntityFrameworkCore.DbUpdateException", written, StringComparison.Ordinal);
        Assert.Contains("SqlErrorNumber=2601", written, StringComparison.Ordinal);
        Assert.DoesNotContain(oid, written, StringComparison.Ordinal);
        Assert.Empty(logs.Exceptions);
    }

    [Fact]
    public async Task Ef_update_errors_stay_out_of_the_log_and_our_write_failure_line_stays_in()
    {
        const string oid = "oid-ef-update-secret";
        var root = RepoRoot();
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "backend", "PFOH.Api", "appsettings.json"))
            .AddJsonFile(Path.Combine(root, "backend", "PFOH.Api", "appsettings.Development.json"))
            .Build();
        var logs = new ListLogger();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddConfiguration(config.GetSection("Logging"));
            builder.AddProvider(new ListLoggerProvider(logs));
        });

        var ef = factory.CreateLogger("Microsoft.EntityFrameworkCore.Update");
        Assert.False(ef.IsEnabled(LogLevel.Critical));
        ef.LogError(
            SqlError(2627, $"The duplicate key value is ({oid})."),
            "An exception occurred in the database while saving changes.");

        var database = $"profile-ef-log-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseInMemoryDatabase(database)
            .AddInterceptors(new FailFirstSaveInterceptor(oid))
            .Options;
        await using (var setup = new PfohDbContext(new DbContextOptionsBuilder<PfohDbContext>().UseInMemoryDatabase(database).Options))
        {
            setup.Database.EnsureCreated();
            setup.UserProfiles.Add(ProfileRow(oid, "2145551234", "Kept Org"));
            setup.SaveChanges();
        }

        await using (var db = new PfohDbContext(options))
        {
            var service = new UserProfileService(db, factory.CreateLogger<UserProfileService>(), new ConfigurationBuilder().Build());
            var result = await service.UpdateOwnAsync(
                ProfileUser(oid, "ada@example.com", "Ada Person"),
                oid,
                oid,
                "Ada Updated",
                "214-555-1234",
                "Kept Org",
                CancellationToken.None);
            Assert.Null(result.Error);
        }

        var written = string.Join("\n", logs.Lines);
        Assert.Contains("Retrying profile save after a write conflict.", written, StringComparison.Ordinal);
        Assert.Contains("SqlErrorNumber=2601", written, StringComparison.Ordinal);
        Assert.DoesNotContain(oid, written, StringComparison.Ordinal);
        Assert.Empty(logs.Exceptions);
    }

    [Fact]
    public async Task Raced_first_sign_in_logs_the_sql_number_and_keeps_the_oid_out_of_app_insights()
    {
        const string oid = "oid-race-command-secret";
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "backend", "PFOH.Api", "appsettings.json"))
            .Build();
        var channel = new CaptureChannel();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(config.GetSection("Logging"));
            logging.AddApplicationInsights(
                telemetry =>
                {
                    telemetry.ConnectionString = "InstrumentationKey=11111111-1111-1111-1111-111111111111";
                    telemetry.TelemetryChannel = channel;
                },
                _ => { });
            // Same rule AddApplicationInsightsTelemetry inserts at the front of the list.
            logging.Services.Configure<LoggerFilterOptions>(options => options.Rules.Insert(
                0,
                new LoggerFilterRule(
                    "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider",
                    null,
                    LogLevel.Warning,
                    null)));
        });
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        var updates = factory.CreateLogger("Microsoft.EntityFrameworkCore.Update");
        var commands = factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");
        Assert.False(updates.IsEnabled(LogLevel.Error));
        Assert.False(commands.IsEnabled(LogLevel.Error));
        Assert.True(factory.CreateLogger<UserProfileService>().IsEnabled(LogLevel.Warning));
        updates.LogError(SqlError(2627, $"The duplicate key value is ({oid})."), "An exception occurred in the database while saving changes.");
        commands.LogError(SqlError(2601, $"The duplicate key value is ({oid})."), "Failed executing DbCommand for " + oid + ".");

        var database = $"profile-race-log-{Guid.NewGuid():N}";
        var plain = new DbContextOptionsBuilder<PfohDbContext>().UseInMemoryDatabase(database).Options;
        await using (var setup = new PfohDbContext(plain))
        {
            setup.Database.EnsureCreated();
        }

        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseInMemoryDatabase(database)
            .UseLoggerFactory(factory)
            .AddInterceptors(new RacedSqlInsertInterceptor(plain, oid))
            .Options;
        await using var db = new PfohDbContext(options);
        var service = new UserProfileService(db, factory.CreateLogger<UserProfileService>(), new ConfigurationBuilder().Build());
        await service.EnsureProvisionedAsync(ProfileUser(oid, "race@example.com", "Race Person"), CancellationToken.None);
        provider.GetService<TelemetryClient>()?.Flush();

        var written = string.Join("\n", channel.Items.Select(TelemetryText));
        Assert.Contains("Profile insert raced. Reading the existing row.", written, StringComparison.Ordinal);
        Assert.Contains("ExceptionType=Microsoft.EntityFrameworkCore.DbUpdateException", written, StringComparison.Ordinal);
        Assert.Contains("SqlErrorNumber=2627", written, StringComparison.Ordinal);
        Assert.DoesNotContain(oid, written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_duplicate_store_failure_waits_five_minutes_before_the_next_attempt()
    {
        const string secret = "oid-store-backoff-secret";
        UserProfileSchema.ResetForTests();
        var state = new UserProfileService.StoreAttemptState();
        var clock = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
        state.UtcNow = () => clock;
        UserProfileService.StoreAttemptForTests.Value = state;
        var attempts = 0;
        UserProfileSchema.EnsureFailureForTests.Value = _ =>
        {
            attempts++;
            throw SqlError(1205, $"Could not prepare the profile table for {secret}.");
        };
        var logs = new ListLogger();
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PfohDbContext>().UseSqlite(connection).Options;
            await using var db = new PfohDbContext(options);
            var service = new UserProfileService(db, logs, new ConfigurationBuilder().Build());
            var user = ProfileUser("oid-backoff-user", "backoff@example.com", "Backoff Person");

            var first = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.EnsureProvisionedAsync(user, CancellationToken.None));
            Assert.Equal(1, attempts);
            Assert.NotNull(first.InnerException);

            clock = clock.AddMinutes(5).AddTicks(-1);
            var skipped = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.EnsureProvisionedAsync(user, CancellationToken.None));
            Assert.Null(skipped.InnerException);
            Assert.Equal(1, attempts);
            Assert.Single(logs.Lines, line => line.Contains("Unable to prepare the user profile table.", StringComparison.Ordinal));

            clock = clock.AddTicks(1);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.EnsureProvisionedAsync(user, CancellationToken.None));
            Assert.Equal(2, attempts);

            var written = string.Join("\n", logs.Lines);
            Assert.Equal(2, logs.Lines.Count(line => line.Contains("Unable to prepare the user profile table.", StringComparison.Ordinal)));
            Assert.Contains("ExceptionType=Microsoft.Data.SqlClient.SqlException", written, StringComparison.Ordinal);
            Assert.Contains("SqlErrorNumber=1205", written, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, written, StringComparison.Ordinal);
            Assert.Empty(logs.Exceptions);
        }
        finally
        {
            UserProfileSchema.EnsureFailureForTests.Value = null;
            UserProfileService.ResetStoreFailureForTests();
            UserProfileSchema.ResetForTests();
        }
    }

    [Fact]
    public async Task Duplicate_owner_rows_do_not_start_the_store_backoff()
    {
        const string oid = "oid-dup-backoff";
        UserProfileSchema.ResetForTests();
        var state = new UserProfileService.StoreAttemptState();
        UserProfileService.StoreAttemptForTests.Value = state;
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE UserProfiles (
                        Id INTEGER NOT NULL CONSTRAINT PK_UserProfiles PRIMARY KEY,
                        OwnerObjectId TEXT NULL,
                        DisplayName TEXT NOT NULL,
                        Email TEXT NOT NULL,
                        Phone TEXT NOT NULL,
                        Organization TEXT NULL,
                        DisplayNameSource TEXT NULL,
                        PhoneSource TEXT NULL,
                        OrganizationSource TEXT NULL,
                        CreatedUtc TEXT NOT NULL,
                        UpdatedUtc TEXT NOT NULL
                    );
                    INSERT INTO UserProfiles (
                        Id, OwnerObjectId, DisplayName, Email, Phone, Organization,
                        DisplayNameSource, PhoneSource, OrganizationSource, CreatedUtc, UpdatedUtc)
                    VALUES
                        (1, 'oid-dup-backoff', 'Newer', 'dup@example.com', '', NULL, 'claim', 'claim', 'claim', '2024-01-01T00:00:00', '2024-01-01T00:00:00'),
                        (2, 'oid-dup-backoff', 'Older', 'dup@example.com', '', NULL, 'claim', 'claim', 'claim', '2020-01-01T00:00:00', '2020-01-01T00:00:00');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<PfohDbContext>().UseSqlite(connection).Options;
            await using var db = new PfohDbContext(options);
            var service = new UserProfileService(db, NullLogger<UserProfileService>.Instance, new ConfigurationBuilder().Build());
            var user = ProfileUser(oid, "dup@example.com", "Chosen Name");
            await service.EnsureProvisionedAsync(user, CancellationToken.None);
            await service.EnsureProvisionedAsync(user, CancellationToken.None);
            Assert.Equal(0, state.RetryAfterTicks);
        }
        finally
        {
            UserProfileService.ResetStoreFailureForTests();
            UserProfileSchema.ResetForTests();
        }
    }

    [Fact]
    public async Task Get_and_sign_in_choose_the_same_duplicated_row()
    {
        const string oid = "oid-same-row";
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var database = $"profile-same-row-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<PfohDbContext>().UseInMemoryDatabase(database).Options;
        await using (var setup = new PfohDbContext(options))
        {
            setup.Database.EnsureCreated();
            setup.UserProfiles.Add(ProfileAt(1, oid, newer, "Newer Row", "+12145550002", "Newer Org"));
            setup.UserProfiles.Add(ProfileAt(2, oid, older, "Older Row", "+12145550001", null));
            setup.SaveChanges();
        }

        await using var db = new PfohDbContext(options);
        var service = new UserProfileService(db, NullLogger<UserProfileService>.Instance, new ConfigurationBuilder().Build());
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", oid),
            new Claim("preferred_username", "same@example.com"),
            new Claim("name", "Chosen Name"),
            new Claim("organization", "From Token")
        ], "Test"));

        await service.EnsureProvisionedAsync(user, CancellationToken.None);
        var controller = new ProfileController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
        var action = await controller.Get(CancellationToken.None);
        var dto = action.Result is OkObjectResult ok
            ? Assert.IsType<UserProfileDto>(ok.Value)
            : action.Value;
        Assert.NotNull(dto);
        Assert.Equal("Older Row", dto!.DisplayName);
        Assert.Equal("From Token", dto.Organization);
        Assert.Equal("+12145550001", dto.Phone);

        var rows = await db.UserProfiles.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal("From Token", rows.Single(row => row.Id == 2).Organization);
        Assert.Equal("Newer Org", rows.Single(row => row.Id == 1).Organization);
        Assert.Equal("Older Row", rows.Single(row => row.Id == 2).DisplayName);
        Assert.Equal("Newer Row", rows.Single(row => row.Id == 1).DisplayName);
    }

    [Fact]
    public void Startup_index_failure_logs_the_sql_number_only()
    {
        const string oid = "oid-index-1505-secret";
        var logs = new ListLogger();
        UserProfileService.LogStartupIndexFailure(
            logs,
            SqlError(1505, $"The duplicate key value is ({oid})."));

        var written = string.Join("\n", logs.Lines);
        Assert.Contains("STARTUP CLEANUP FAILED while ensuring dbo.UserProfiles. The site will keep running.", written, StringComparison.Ordinal);
        Assert.Contains("SqlErrorNumber=1505", written, StringComparison.Ordinal);
        Assert.DoesNotContain(oid, written, StringComparison.Ordinal);
        Assert.Empty(logs.Exceptions);
    }

    [Fact]
    public async Task Sign_in_with_a_duplicated_owner_id_updates_the_oldest_row()
    {
        const string oid = "oid-duplicate-choice";
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var tied = new DateTime(2021, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var byAge = await ProvisionDuplicatesAsync(oid, new[]
        {
            (id: 1, created: newer, name: "Newer Row"),
            (id: 2, created: older, name: "Older Row")
        });
        Assert.Equal("Chosen Name", byAge.Single(row => row.Id == 2).DisplayName);
        Assert.Equal("Newer Row", byAge.Single(row => row.Id == 1).DisplayName);

        var byId = await ProvisionDuplicatesAsync(oid, new[]
        {
            (id: 10, created: tied, name: "Higher Id"),
            (id: 4, created: tied, name: "Lower Id")
        });
        Assert.Equal("Chosen Name", byId.Single(row => row.Id == 4).DisplayName);
        Assert.Equal("Higher Id", byId.Single(row => row.Id == 10).DisplayName);
    }

    private static async Task<List<UserProfile>> ProvisionDuplicatesAsync(
        string oid,
        IReadOnlyList<(int id, DateTime created, string name)> rows)
    {
        var database = $"profile-duplicate-choice-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<PfohDbContext>().UseInMemoryDatabase(database).Options;
        await using (var setup = new PfohDbContext(options))
        {
            setup.Database.EnsureCreated();
            foreach (var row in rows)
            {
                setup.UserProfiles.Add(new UserProfile
                {
                    Id = row.id,
                    OwnerObjectId = oid,
                    DisplayName = row.name,
                    Email = "same@example.com",
                    Phone = string.Empty,
                    DisplayNameSource = ProfileFieldSource.Claim,
                    PhoneSource = ProfileFieldSource.Claim,
                    OrganizationSource = ProfileFieldSource.Claim,
                    CreatedUtc = row.created,
                    UpdatedUtc = row.created
                });
            }

            setup.SaveChanges();
        }

        await using var db = new PfohDbContext(options);
        var service = new UserProfileService(db, NullLogger<UserProfileService>.Instance, new ConfigurationBuilder().Build());
        await service.EnsureProvisionedAsync(ProfileUser(oid, "same@example.com", "Chosen Name"), CancellationToken.None);
        return await db.UserProfiles.OrderBy(row => row.Id).ToListAsync();
    }

    private static UserProfile ProfileAt(int id, string oid, DateTime created, string name, string phone, string? organization)
    {
        return new UserProfile
        {
            Id = id,
            OwnerObjectId = oid,
            DisplayName = name,
            Email = "same@example.com",
            Phone = phone,
            Organization = organization,
            DisplayNameSource = ProfileFieldSource.Profile,
            PhoneSource = ProfileFieldSource.Profile,
            OrganizationSource = ProfileFieldSource.Profile,
            CreatedUtc = created,
            UpdatedUtc = created
        };
    }

    private static string TelemetryText(ITelemetry item)
    {
        var text = item switch
        {
            TraceTelemetry trace => trace.Message,
            ExceptionTelemetry exception => exception.Message + " " + exception.Exception,
            _ => item.ToString() ?? string.Empty
        };
        if (item is ISupportProperties properties)
        {
            text += " " + string.Join(" ", properties.Properties.Values);
        }

        return text;
    }

    private sealed class CaptureChannel : ITelemetryChannel
    {
        public List<ITelemetry> Items { get; } = new();
        public bool? DeveloperMode { get; set; } = true;
        public string? EndpointAddress { get; set; }
        public void Send(ITelemetry item) => Items.Add(item);
        public void Flush() { }
        public void Dispose() { }
    }

    private static UserProfile ProfileRow(string oid, string phone, string organization)
    {
        var now = DateTime.UtcNow;
        return new UserProfile
        {
            OwnerObjectId = oid,
            DisplayName = "Ada Person",
            Email = "ada@example.com",
            Phone = phone,
            Organization = organization,
            DisplayNameSource = ProfileFieldSource.Profile,
            PhoneSource = ProfileFieldSource.Profile,
            OrganizationSource = ProfileFieldSource.Profile,
            CreatedUtc = now,
            UpdatedUtc = now
        };
    }

    private static ClaimsPrincipal ProfileUser(string oid, string email, string name, string? phone = null)
    {
        var claims = new List<Claim>
        {
            new("oid", oid),
            new("preferred_username", email),
            new("name", name)
        };
        if (phone is not null)
        {
            claims.Add(new Claim("phone", phone));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static SqlException SqlError(int number, string? message = null)
    {
        var text = message ?? "provider error";
        var assembly = typeof(SqlException).Assembly;
        var errorType = assembly.GetType("Microsoft.Data.SqlClient.SqlError", throwOnError: true)!;
        var error = Activator.CreateInstance(
            errorType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { number, (byte)0, (byte)14, "server", text, "proc", 1, (uint)0, null },
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
        return (SqlException)ctor!.Invoke(new object?[] { text, collection, null, Guid.Empty });
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

    private sealed class RacedSqlInsertInterceptor(DbContextOptions<PfohDbContext> options, string ownerObjectId) : SaveChangesInterceptor
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
                var created = DateTime.UtcNow;
                other.UserProfiles.Add(new UserProfile
                {
                    OwnerObjectId = ownerObjectId,
                    DisplayName = "Existing",
                    Email = "existing@example.com",
                    Phone = string.Empty,
                    DisplayNameSource = ProfileFieldSource.Claim,
                    PhoneSource = ProfileFieldSource.Claim,
                    OrganizationSource = ProfileFieldSource.Claim,
                    CreatedUtc = created,
                    UpdatedUtc = created
                });
                other.SaveChanges();
                var message = $"Cannot insert duplicate key row. The duplicate key value is ({ownerObjectId}).";
                throw new DbUpdateException(message, SqlError(2627, message));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FailFirstSaveInterceptor(string oid) : SaveChangesInterceptor
    {
        private int hits;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref hits) == 1)
            {
                throw DuplicateKey(oid);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FailSecondSaveInterceptor(string oid) : SaveChangesInterceptor
    {
        private int hits;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref hits) == 2)
            {
                throw DuplicateKey(oid);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (hits == 1 && eventData.Context is not null)
            {
                var profile = eventData.Context.Set<UserProfile>().Local.Single();
                var phone = eventData.Context.Entry(profile).Property(item => item.Phone);
                phone.CurrentValue = string.Empty;
                phone.OriginalValue = string.Empty;
                phone.IsModified = false;
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static DbUpdateException DuplicateKey(string oid)
    {
        var message = $"Cannot insert duplicate key row. The duplicate key value is ({oid}).";
        return new DbUpdateException(message, SqlError(2601, message));
    }

    private sealed class ListLogger : ILogger<UserProfileService>
    {
        public List<string> Lines { get; } = new();
        public List<Exception> Exceptions { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class ListLoggerProvider(ListLogger logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logs;

        public void Dispose() { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
