using System.Collections;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PFOH.Api.Data;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

/// <summary>
/// The schema skip key is the server and database, not the connection string.
/// SqlClient removes the password after the connection opens.
/// </summary>
[Collection("UserProfileSchema")]
public class UserProfileSchemaKeyTests
{
    private const string Password = "secret-value";
    private const string Server = "tcp:prod.database.windows.net,1433";
    private const string Catalog = "PfohFlags";

    [Fact]
    public async Task Opening_the_connection_strips_the_password_and_the_schema_step_does_not_rerun()
    {
        UserProfileSchema.ResetForTests();
        var connection = new StrippingSqlConnection(SqlConnectionString(Server, Catalog, Password, "pfoh_app"));
        try
        {
            await using var db = new PfohDbContext(SqlOptions(connection));
            Assert.Contains("Password=" + Password, connection.ConnectionString, StringComparison.Ordinal);

            var ran = await UserProfileSchema.EnsureAsync(db, NullLogger.Instance, CancellationToken.None);

            Assert.True(ran);
            Assert.Equal(SchemaBatches, connection.Statements);
            Assert.DoesNotContain(Password, connection.ConnectionString, StringComparison.Ordinal);
            Assert.Equal(ConnectionState.Closed, connection.State);

            var skipped = await UserProfileSchema.EnsureAsync(db, NullLogger.Instance, CancellationToken.None);

            Assert.False(skipped);
            Assert.Equal(SchemaBatches, connection.Statements);
            AssertStoredKey(Server, Catalog);

            await using var sameDatabase = new StrippingSqlConnection(
                SqlConnectionString(Server.ToUpperInvariant(), Catalog.ToLowerInvariant(), "other-secret", "other_user"));
            await using var sameDb = new PfohDbContext(SqlOptions(sameDatabase));
            var stillSkipped = await UserProfileSchema.EnsureAsync(sameDb, NullLogger.Instance, CancellationToken.None);

            Assert.False(stillSkipped);
            Assert.Empty(sameDatabase.Statements);
            AssertStoredKey(Server, Catalog);
        }
        finally
        {
            await connection.DisposeAsync();
            UserProfileSchema.ResetForTests();
        }
    }

    [Fact]
    public async Task A_different_initial_catalog_runs_the_schema_step_again()
    {
        UserProfileSchema.ResetForTests();
        var first = new StrippingSqlConnection(SqlConnectionString(Server, Catalog, Password, "pfoh_app"));
        var second = new StrippingSqlConnection(SqlConnectionString(Server, "OtherFlags", Password, "pfoh_app"));
        try
        {
            await using var firstDb = new PfohDbContext(SqlOptions(first));
            await using var secondDb = new PfohDbContext(SqlOptions(second));

            Assert.True(await UserProfileSchema.EnsureAsync(firstDb, NullLogger.Instance, CancellationToken.None));
            Assert.Equal(SchemaBatches, first.Statements);

            Assert.True(await UserProfileSchema.EnsureAsync(secondDb, NullLogger.Instance, CancellationToken.None));
            Assert.Equal(SchemaBatches, second.Statements);
            AssertStoredKey(Server, "OtherFlags");
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            UserProfileSchema.ResetForTests();
        }
    }

    [Fact]
    public async Task Each_sqlite_memory_connection_still_runs_the_schema_step_on_its_own()
    {
        UserProfileSchema.ResetForTests();
        await using var first = new SqliteConnection("Data Source=:memory:");
        await first.OpenAsync();
        await using var second = new SqliteConnection("Data Source=:memory:");
        await second.OpenAsync();
        try
        {
            await using var firstDb = new PfohDbContext(SqliteOptions(first));
            await using var secondDb = new PfohDbContext(SqliteOptions(second));

            Assert.True(await UserProfileSchema.EnsureAsync(firstDb, NullLogger.Instance, CancellationToken.None));
            Assert.False(await UserProfileSchema.EnsureAsync(firstDb, NullLogger.Instance, CancellationToken.None));
            Assert.True(await UserProfileSchema.EnsureAsync(secondDb, NullLogger.Instance, CancellationToken.None));
            Assert.False(await UserProfileSchema.EnsureAsync(secondDb, NullLogger.Instance, CancellationToken.None));
        }
        finally
        {
            UserProfileSchema.ResetForTests();
        }
    }

    [Fact]
    public async Task Display_name_read_failure_logs_the_exception_type_and_sql_numbers_only()
    {
        const string secret = "display-name-secret-value";
        UserProfileSchema.ResetForTests();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var logs = new ListLogger();
        try
        {
            var options = new DbContextOptionsBuilder<PfohDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(new DisplayNameReadFailure(secret))
                .Options;
            await using var db = new PfohDbContext(options);
            var service = new UserProfileService(db, logs, new ConfigurationBuilder().Build());
            var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "oid-display"),
                new Claim("name", "Token Name")
            ], "Test"));

            var name = await service.ResolveDisplayNameAsync(user, CancellationToken.None);

            Assert.Equal("Token Name", name);
            var written = string.Join("\n", logs.Lines);
            Assert.Contains(
                "Saved profile display name could not be read. Using the token name.",
                written,
                StringComparison.Ordinal);
            Assert.Contains(
                "ExceptionType=Microsoft.Data.SqlClient.SqlException",
                written,
                StringComparison.Ordinal);
            Assert.Contains("SqlErrorNumbers=40613,18456", written, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, written, StringComparison.Ordinal);
            Assert.DoesNotContain(" at ", written, StringComparison.Ordinal);
            Assert.Empty(logs.Exceptions);
        }
        finally
        {
            UserProfileSchema.ResetForTests();
        }
    }

    private static void AssertStoredKey(string server, string catalog)
    {
        var key = typeof(UserProfileSchema)
            .GetField("ensuredDatabaseKey", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null) as string;
        Assert.Equal(
            "sql:" + server.Trim().ToUpperInvariant() + "\n" + catalog.Trim().ToUpperInvariant(),
            key);
        Assert.DoesNotContain("Password", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, key, StringComparison.Ordinal);
        Assert.DoesNotContain("pfoh_app", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other-secret", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other_user", key, StringComparison.OrdinalIgnoreCase);
    }

    private static string SqlConnectionString(string server, string catalog, string password, string user) =>
        $"Data Source={server};Initial Catalog={catalog};User ID={user};Password={password};Persist Security Info=false;Connect Timeout=1";

    private static DbContextOptions<PfohDbContext> SqlOptions(DbConnection connection) =>
        new DbContextOptionsBuilder<PfohDbContext>().UseSqlServer(connection).Options;

    private static DbContextOptions<PfohDbContext> SqliteOptions(DbConnection connection) =>
        new DbContextOptionsBuilder<PfohDbContext>().UseSqlite(connection).Options;

    private static IReadOnlyList<string> SchemaBatches { get; } =
    [
        UserProfileSchema.CreateSql,
        UserProfileSchema.AddPhoneAndOrganizationSql,
        UserProfileSchema.AddClaimSourceColumnsSql,
        UserProfileSchema.EnsureOwnerObjectIdUniqueIndexSql
    ];

    /// <summary>
    /// Mimics SqlClient with Persist Security Info false: the first open removes the password
    /// and later reads of <see cref="DbConnection.ConnectionString"/> do not put it back.
    /// Commands succeed so the schema batches can be counted without a server.
    /// </summary>
    private sealed class StrippingSqlConnection : DbConnection
    {
        private string revealed;
        private ConnectionState state = ConnectionState.Closed;
        private bool openedOnce;

        public StrippingSqlConnection(string connectionString)
        {
            revealed = connectionString;
        }

        public List<string> Statements { get; } = [];

        [AllowNull]
        public override string ConnectionString
        {
            get => revealed;
            set => revealed = value ?? string.Empty;
        }

        public override string Database => new SqlConnectionStringBuilder(revealed).InitialCatalog;

        public override string DataSource => new SqlConnectionStringBuilder(revealed).DataSource;

        public override string ServerVersion => "15.0.2000";

        public override ConnectionState State => state;

        public override void ChangeDatabase(string databaseName) =>
            throw new NotSupportedException();

        public override void Open()
        {
            if (state == ConnectionState.Open)
            {
                return;
            }

            if (!openedOnce)
            {
                var builder = new SqlConnectionStringBuilder(revealed);
                if (!builder.PersistSecurityInfo)
                {
                    builder.Remove("Password");
                    revealed = builder.ConnectionString;
                }

                openedOnce = true;
            }

            state = ConnectionState.Open;
        }

        public override void Close() => state = ConnectionState.Closed;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new NoopTransaction(this, isolationLevel);

        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);

        private sealed class RecordingCommand(StrippingSqlConnection connection) : DbCommand
        {
            private readonly ParameterList parameters = new();

            [AllowNull]
            public override string CommandText { get; set; } = string.Empty;

            public override int CommandTimeout { get; set; } = 30;

            public override CommandType CommandType { get; set; } = CommandType.Text;

            public override bool DesignTimeVisible { get; set; } = true;

            public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

            protected override DbConnection? DbConnection { get; set; } = connection;

            protected override DbParameterCollection DbParameterCollection => parameters;

            protected override DbTransaction? DbTransaction { get; set; }

            public override void Cancel()
            {
            }

            public override int ExecuteNonQuery()
            {
                connection.Statements.Add(CommandText);
                return 0;
            }

            public override object? ExecuteScalar() => 0;

            public override void Prepare()
            {
            }

            protected override DbParameter CreateDbParameter() => new StoredParameter();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
                throw new NotSupportedException();
        }

        private sealed class StoredParameter : DbParameter
        {
            public override DbType DbType { get; set; }

            public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

            public override bool IsNullable { get; set; }

            [AllowNull]
            public override string ParameterName { get; set; } = string.Empty;

            public override int Size { get; set; }

            [AllowNull]
            public override string SourceColumn { get; set; } = string.Empty;

            public override bool SourceColumnNullMapping { get; set; }

            public override object? Value { get; set; }

            public override void ResetDbType() => DbType = DbType.String;
        }

        private sealed class ParameterList : DbParameterCollection
        {
            private readonly List<DbParameter> items = [];

            public override int Count => items.Count;

            public override bool IsFixedSize => false;

            public override bool IsReadOnly => false;

            public override bool IsSynchronized => false;

            public override object SyncRoot { get; } = new();

            public override int Add(object value)
            {
                items.Add((DbParameter)value);
                return items.Count - 1;
            }

            public override void AddRange(Array values)
            {
                foreach (var value in values)
                {
                    Add(value!);
                }
            }

            public override void Clear() => items.Clear();

            public override bool Contains(object value) => items.Contains((DbParameter)value);

            public override bool Contains(string value) => IndexOf(value) >= 0;

            public override void CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);

            public override IEnumerator GetEnumerator() => items.GetEnumerator();

            public override int IndexOf(object value) => items.IndexOf((DbParameter)value);

            public override int IndexOf(string parameterName) =>
                items.FindIndex(item => string.Equals(item.ParameterName, parameterName, StringComparison.Ordinal));

            public override void Insert(int index, object value) => items.Insert(index, (DbParameter)value);

            public override void Remove(object value) => items.Remove((DbParameter)value);

            public override void RemoveAt(int index) => items.RemoveAt(index);

            public override void RemoveAt(string parameterName)
            {
                var index = IndexOf(parameterName);
                if (index >= 0)
                {
                    items.RemoveAt(index);
                }
            }

            protected override DbParameter GetParameter(int index) => items[index];

            protected override DbParameter GetParameter(string parameterName) => items[IndexOf(parameterName)];

            protected override void SetParameter(int index, DbParameter value) => items[index] = value;

            protected override void SetParameter(string parameterName, DbParameter value) =>
                items[IndexOf(parameterName)] = value;
        }

        private sealed class NoopTransaction(DbConnection connection, IsolationLevel isolation) : DbTransaction
        {
            public override IsolationLevel IsolationLevel { get; } = isolation;

            protected override DbConnection? DbConnection { get; } = connection;

            public override void Commit()
            {
            }

            public override void Rollback()
            {
            }
        }
    }

    private sealed class DisplayNameReadFailure(string secret) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result) =>
            throw Failure();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            throw Failure();

        private SqlException Failure()
        {
            var assembly = typeof(SqlException).Assembly;
            var errorType = assembly.GetType("Microsoft.Data.SqlClient.SqlError", throwOnError: true)!;
            var collectionType = assembly.GetType("Microsoft.Data.SqlClient.SqlErrorCollection", throwOnError: true)!;
            var collection = Activator.CreateInstance(
                collectionType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: Array.Empty<object>(),
                culture: null);
            var add = collectionType.GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var number in new[] { 40613, 18456 })
            {
                var error = Activator.CreateInstance(
                    errorType,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    args: new object?[] { number, (byte)0, (byte)14, "server", secret, "proc", 1, (uint)0, null },
                    culture: null);
                add.Invoke(collection, [error]);
            }

            var ctor = typeof(SqlException).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string), collectionType, typeof(Exception), typeof(Guid)],
                modifiers: null);
            return (SqlException)ctor!.Invoke([secret, collection, null, Guid.Empty]);
        }
    }

    private sealed class ListLogger : ILogger<UserProfileService>
    {
        public List<string> Lines { get; } = [];

        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
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

            public void Dispose()
            {
            }
        }
    }
}
