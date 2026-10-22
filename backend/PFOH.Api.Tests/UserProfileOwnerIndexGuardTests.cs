using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PFOH.Api.Data;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class UserProfileOwnerIndexGuardTests
{
    private const string DuplicateOid = "oid-duplicate-secret";

    [Fact]
    public async Task Duplicates_leave_sqlite_without_an_index_and_do_not_change_rows()
    {
        UserProfileSchema.ResetForTests();
        await using var connection = await OpenProfileDatabaseAsync(ownerNotNull: false);
        await InsertAsync(connection, 1, DuplicateOid, "First");
        await InsertAsync(connection, 2, DuplicateOid, "Second");
        await InsertAsync(connection, 3, "oid-unique", "Third");
        await InsertAsync(connection, 4, null, "Null owner");
        var before = await ReadRowsAsync(connection);

        await using var db = new PfohDbContext(Options(connection));
        var logs = new List<string>();
        await UserProfileSchema.EnsureAsync(db, new ListLogger(logs), CancellationToken.None);

        Assert.Empty(await OwnerIndexSqlAsync(connection));
        Assert.Equal(before, await ReadRowsAsync(connection));
        var guard = Assert.Single(logs, line => line.Contains("duplicate OwnerObjectId groups", StringComparison.Ordinal));
        Assert.Contains("1", guard, StringComparison.Ordinal);
        Assert.DoesNotContain(DuplicateOid, guard, StringComparison.Ordinal);
        Assert.DoesNotContain("oid-unique", guard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_duplicates_create_a_sqlite_unique_index()
    {
        UserProfileSchema.ResetForTests();
        await using var connection = await OpenProfileDatabaseAsync(ownerNotNull: true);
        await InsertAsync(connection, 1, "oid-a", "A");
        await InsertAsync(connection, 2, "oid-b", "B");
        var before = await ReadRowsAsync(connection);

        await using var db = new PfohDbContext(Options(connection));
        await UserProfileSchema.EnsureAsync(db, new ListLogger(new List<string>()), CancellationToken.None);

        var indexes = await OwnerIndexSqlAsync(connection);
        var index = Assert.Single(indexes);
        Assert.Contains("UNIQUE", index, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OwnerObjectId", index, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE", index, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await ReadRowsAsync(connection));
    }

    [Fact]
    public async Task Nullable_owner_ids_get_a_filtered_unique_index_when_there_are_no_duplicates()
    {
        UserProfileSchema.ResetForTests();
        await using var connection = await OpenProfileDatabaseAsync(ownerNotNull: false);
        await InsertAsync(connection, 1, "oid-a", "A");
        await InsertAsync(connection, 2, "oid-b", "B");
        await InsertAsync(connection, 3, null, "Null one");
        await InsertAsync(connection, 4, null, "Null two");
        var before = await ReadRowsAsync(connection);

        await using var db = new PfohDbContext(Options(connection));
        await UserProfileSchema.EnsureAsync(db, new ListLogger(new List<string>()), CancellationToken.None);

        var index = Assert.Single(await OwnerIndexSqlAsync(connection));
        Assert.Contains("WHERE", index, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OwnerObjectId IS NOT NULL", index, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await ReadRowsAsync(connection));
    }

    [Fact]
    public void Sql_server_index_batch_checks_duplicates_inside_a_transaction_and_throws()
    {
        var sql = UserProfileSchema.EnsureOwnerObjectIdUniqueIndexSql;
        Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
        Assert.Contains(UserProfileSchema.DuplicateOwnerObjectIdIndexMessage, sql, StringComparison.Ordinal);
        Assert.Contains("is_nullable = 1", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE [OwnerObjectId] IS NOT NULL", sql, StringComparison.Ordinal);
        var migration = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "backend",
            "PFOH.Api",
            "Data",
            "Migrations",
            "20261022120000_AddUserProfileOwnerObjectIdUniqueIndex.cs"));
        Assert.Contains("UserProfileSchema.EnsureOwnerObjectIdUniqueIndexSql", migration, StringComparison.Ordinal);
        Assert.False(UserProfileSchema.IsDuplicateOwnerObjectIdBlock(new InvalidOperationException("duplicate oid-value 2601")));
        Assert.True(UserProfileSchema.IsDuplicateOwnerObjectIdBlock(
            new InvalidOperationException(UserProfileSchema.DuplicateOwnerObjectIdIndexMessage)));
    }

    [Fact]
    public async Task Duplicate_rows_still_let_the_app_start_and_sign_in_does_not_add_a_row()
    {
        UserProfileSchema.ResetForTests();
        await using var factory = new DuplicateOwnerAppFactory();
        var before = await factory.ReadRowsAsync();
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add(TestAuthHandler.OidHeader, DuplicateOid);
        request.Headers.Add(TestAuthHandler.EmailHeader, "dup@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Duplicate Person");
        var profile = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);

        Assert.Equal(before, await factory.ReadRowsAsync());
        Assert.Empty(await factory.OwnerIndexSqlAsync());
        var guard = Assert.Single(factory.Logs, line => line.Contains("duplicate OwnerObjectId groups", StringComparison.Ordinal));
        Assert.Contains("1", guard, StringComparison.Ordinal);
        Assert.DoesNotContain(DuplicateOid, guard, StringComparison.Ordinal);
    }

    private static DbContextOptions<PfohDbContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<PfohDbContext>().UseSqlite(connection).Options;

    private static async Task<SqliteConnection> OpenProfileDatabaseAsync(bool ownerNotNull)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var nullability = ownerNotNull ? "NOT NULL" : "NULL";
        await ExecuteAsync(connection, $"""
            CREATE TABLE UserProfiles (
                Id INTEGER NOT NULL CONSTRAINT PK_UserProfiles PRIMARY KEY,
                OwnerObjectId TEXT {nullability},
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
            """);
        return connection;
    }

    private static Task InsertAsync(SqliteConnection connection, int id, string? owner, string name) =>
        ExecuteAsync(connection, $"""
            INSERT INTO UserProfiles (
                Id, OwnerObjectId, DisplayName, Email, Phone, Organization,
                DisplayNameSource, PhoneSource, OrganizationSource, CreatedUtc, UpdatedUtc)
            VALUES (
                {id}, {Sql(owner)}, {Sql(name)}, 'dup@example.com', '', NULL,
                'profile', 'profile', 'profile', '2026-01-15 00:00:00', '2026-01-15 00:00:00');
            """);

    private static async Task<List<string>> ReadRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OwnerObjectId, DisplayName, Email, Phone, Organization,
                   DisplayNameSource, PhoneSource, OrganizationSource, CreatedUtc, UpdatedUtc
            FROM UserProfiles
            ORDER BY Id
            """;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                fields[i] = reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString() ?? "";
            }

            rows.Add(string.Join("|", fields));
        }

        return rows;
    }

    private static async Task<List<string>> OwnerIndexSqlAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'UserProfiles' AND sql IS NOT NULL";
        var indexes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var sql = reader.GetString(0);
            if (sql.Contains("OwnerObjectId", StringComparison.Ordinal))
            {
                indexes.Add(sql);
            }
        }

        return indexes;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Sql(string? value) => value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VERSION")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }

    private sealed class ListLogger(List<string> messages) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class DuplicateOwnerAppFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public List<string> Logs { get; } = new();

        public DuplicateOwnerAppFactory()
        {
            connection.Open();
            ExecuteAsync(connection, """
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
                """).GetAwaiter().GetResult();
            InsertAsync(connection, 1, DuplicateOid, "First").GetAwaiter().GetResult();
            InsertAsync(connection, 2, DuplicateOid, "Second").GetAwaiter().GetResult();
            InsertAsync(connection, 3, "oid-unique", "Kept").GetAwaiter().GetResult();
        }

        public Task<List<string>> ReadRowsAsync() => UserProfileOwnerIndexGuardTests.ReadRowsAsync(connection);

        public Task<List<string>> OwnerIndexSqlAsync() => UserProfileOwnerIndexGuardTests.OwnerIndexSqlAsync(connection);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            UserProfileSchema.ResetForTests();
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

                services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(Logs));
                services.AddDbContext<PfohDbContext>(options => options.UseSqlite(connection));
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

        protected override void ConfigureClient(HttpClient client)
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                connection.Dispose();
            }
        }

        private sealed class ListLoggerProvider(List<string> messages) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new ListLogger(messages);
            public void Dispose() { }
        }
    }
}
