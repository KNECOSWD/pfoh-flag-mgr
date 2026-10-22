using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.UserProfiles when it is missing and adds Phone and Organization
/// when an older table is already present.
/// Production already has the honoree tables and no EF migration history, so a
/// full migration would try to recreate those tables. These statements only
/// touch the profile table. The EF migration
/// <c>AddUserProfilePhoneAndOrganization</c> runs the same SQL when a database
/// is updated through EF.
/// City and state are not account columns. Sponsor city and state stay on
/// dbo.Sponsors so existing sponsor addresses are not deleted.
/// </summary>
public static class UserProfileSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int gateWaiters;
    private static readonly ConditionalWeakTable<DbConnection, string> MemoryDatabaseKeys = new();

    /// <summary>
    /// The database that already ran this batch. A second call for that same database
    /// skips the DDL. Another database in this process, including each SQLite :memory:
    /// connection, still runs it. Tests open many of those connections at once.
    /// This is the server and database name, or a SQLite file path, never a connection string.
    /// </summary>
    private static string? ensuredDatabaseKey;

    /// <summary>
    /// Callers blocked on <see cref="Gate"/>, not the caller that holds it.
    /// Tests use this to prove a waiter re-checks the circuit before the TABLOCKX batch.
    /// </summary>
    internal static int GateWaitersForTests => Volatile.Read(ref gateWaiters);

    /// <summary>
    /// Test-only. When set on the calling async flow, the next schema batch throws this failure
    /// instead of running. Other requests do not see it.
    /// </summary>
    internal static readonly AsyncLocal<Func<CancellationToken, Task>?> EnsureFailureForTests = new();

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[UserProfiles] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_UserProfiles] PRIMARY KEY,
                [OwnerObjectId] nvarchar(128) NOT NULL,
                [DisplayName] nvarchar(100) NOT NULL,
                [Email] nvarchar(255) NOT NULL CONSTRAINT [DF_UserProfiles_Email] DEFAULT (N''),
                [Phone] nvarchar(16) NOT NULL CONSTRAINT [DF_UserProfiles_Phone] DEFAULT (N''),
                [Organization] nvarchar(200) NULL,
                [DisplayNameSource] nvarchar(16) NULL,
                [PhoneSource] nvarchar(16) NULL,
                [OrganizationSource] nvarchar(16) NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [UpdatedUtc] datetime2 NOT NULL,
                CONSTRAINT [UQ_UserProfiles_OwnerObjectId] UNIQUE ([OwnerObjectId])
            );
        END
        """;

    internal const string AddPhoneAndOrganizationSql = """
        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'Phone') IS NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles]
                ADD [Phone] nvarchar(16) NOT NULL CONSTRAINT [DF_UserProfiles_Phone] DEFAULT (N'');
        END

        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'Organization') IS NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles]
                ADD [Organization] nvarchar(200) NULL;
        END
        """;

    /// <summary>
    /// Nullable source flags. No default and no backfill: existing rows stay null,
    /// which provisioning treats as profile-saved so a token cannot overwrite them.
    /// </summary>
    internal const string AddClaimSourceColumnsSql = """
        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'DisplayNameSource') IS NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles] ADD [DisplayNameSource] nvarchar(16) NULL;
        END

        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'PhoneSource') IS NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles] ADD [PhoneSource] nvarchar(16) NULL;
        END

        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'OrganizationSource') IS NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles] ADD [OrganizationSource] nvarchar(16) NULL;
        END
        """;

    /// <summary>
    /// Thrown by the index batch when duplicate object ids exist. The text is the
    /// contract the startup catch recognizes. It does not include any object id.
    /// </summary>
    internal const string DuplicateOwnerObjectIdIndexMessage =
        "UserProfiles has duplicate OwnerObjectId values; unique index not created; resolve duplicates first";

    /// <summary>
    /// Schema only. Creates the unique index when the table has none on OwnerObjectId
    /// and no non-null object id is repeated. Does not update, delete, or insert rows.
    /// A duplicate check failure rolls this batch back and throws
    /// <see cref="DuplicateOwnerObjectIdIndexMessage"/>. A nullable OwnerObjectId
    /// gets a filtered unique index so multiple NULLs are allowed.
    /// </summary>
    internal const string EnsureOwnerObjectIdUniqueIndexSql = """
        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND NOT EXISTS (
                SELECT 1
                FROM sys.indexes AS i
                INNER JOIN sys.index_columns AS ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                INNER JOIN sys.columns AS c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'[dbo].[UserProfiles]')
                  AND i.is_unique = 1
                  AND ic.key_ordinal = 1
                  AND c.name = N'OwnerObjectId'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM sys.index_columns AS ic2
                      WHERE ic2.object_id = i.object_id
                        AND ic2.index_id = i.index_id
                        AND ic2.key_ordinal > 1
                  )
           )
        BEGIN
            DECLARE @opened bit = 0;
            IF @@TRANCOUNT = 0
            BEGIN
                BEGIN TRANSACTION;
                SET @opened = 1;
            END

            DECLARE @duplicateGroups int;
            SELECT @duplicateGroups = COUNT(*)
            FROM (
                SELECT OwnerObjectId, COUNT(*) AS DuplicateRowCount
                FROM dbo.UserProfiles WITH (TABLOCKX, HOLDLOCK)
                WHERE OwnerObjectId IS NOT NULL
                GROUP BY OwnerObjectId
                HAVING COUNT(*) > 1
            ) AS duplicate_groups;

            IF @duplicateGroups > 0
            BEGIN
                IF @opened = 1 AND @@TRANCOUNT > 0
                    ROLLBACK TRANSACTION;
                THROW 50000, N'UserProfiles has duplicate OwnerObjectId values; unique index not created; resolve duplicates first', 1;
            END

            IF EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'[dbo].[UserProfiles]')
                  AND name = N'OwnerObjectId'
                  AND is_nullable = 1
            )
            BEGIN
                CREATE UNIQUE INDEX [IX_UserProfiles_OwnerObjectId]
                    ON [dbo].[UserProfiles] ([OwnerObjectId])
                    WHERE [OwnerObjectId] IS NOT NULL;
            END
            ELSE
            BEGIN
                CREATE UNIQUE INDEX [IX_UserProfiles_OwnerObjectId]
                    ON [dbo].[UserProfiles] ([OwnerObjectId]);
            END

            IF @opened = 1
                COMMIT TRANSACTION;
        END
        """;

    internal const string DuplicateGroupCountSql = """
        SELECT COUNT(*)
        FROM (
            SELECT OwnerObjectId
            FROM dbo.UserProfiles
            WHERE OwnerObjectId IS NOT NULL
            GROUP BY OwnerObjectId
            HAVING COUNT(*) > 1
        ) AS duplicate_groups
        """;

    internal const string DropClaimSourceColumnsSql = """
        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'OrganizationSource') IS NOT NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles] DROP COLUMN [OrganizationSource];
        END

        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'PhoneSource') IS NOT NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles] DROP COLUMN [PhoneSource];
        END

        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'DisplayNameSource') IS NOT NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles] DROP COLUMN [DisplayNameSource];
        END
        """;

    internal const string DropOwnerObjectIdUniqueIndexSql = """
        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'[dbo].[UserProfiles]')
                  AND name = N'IX_UserProfiles_OwnerObjectId'
           )
        BEGIN
            DROP INDEX [IX_UserProfiles_OwnerObjectId] ON [dbo].[UserProfiles];
        END
        """;

    internal const string DropPhoneAndOrganizationSql = """
        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'Organization') IS NOT NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles] DROP COLUMN [Organization];
        END

        IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL
           AND COL_LENGTH(N'[dbo].[UserProfiles]', N'Phone') IS NOT NULL
        BEGIN
            ALTER TABLE [dbo].[UserProfiles] DROP CONSTRAINT [DF_UserProfiles_Phone];
            ALTER TABLE [dbo].[UserProfiles] DROP COLUMN [Phone];
        END
        """;

    public static Task EnsureAsync(PfohDbContext db, CancellationToken ct) =>
        EnsureAsync(db, NullLogger.Instance, ct);

    public static Task<bool> EnsureAsync(PfohDbContext db, ILogger logger, CancellationToken ct) =>
        EnsureAsync(db, logger, ct, circuit: null);

    public static async Task<bool> EnsureAsync(
        PfohDbContext db,
        ILogger logger,
        CancellationToken ct,
        ProfileStoreCircuit? circuit)
    {
        if (!db.Database.IsRelational())
        {
            return false;
        }

        var databaseKey = DatabaseKey(db);
        if (databaseKey == ensuredDatabaseKey)
        {
            circuit?.Close();
            return false;
        }

        var provider = db.Database.ProviderName ?? string.Empty;
        var sqlServer = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        var sqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (!sqlServer && !sqlite)
        {
            return false;
        }

        Interlocked.Increment(ref gateWaiters);
        try
        {
            await Gate.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref gateWaiters);
        }

        var probing = false;
        try
        {
            if (databaseKey == ensuredDatabaseKey)
            {
                circuit?.Close();
                return false;
            }

            // Re-check after the lock. Another request may have opened the circuit
            // while this one waited to run the TABLOCKX index batch.
            if (circuit is not null)
            {
                if (!circuit.TryEnter(circuit.Now))
                {
                    throw new ProfileStoreUnavailableException(circuit.RetryAfter(circuit.Now));
                }

                probing = circuit.State == ProfileStoreCircuitState.HalfOpen;
            }

            async ValueTask ExecuteOnceAsync(CancellationToken token)
            {
                var failForTests = EnsureFailureForTests.Value;
                if (failForTests is not null)
                {
                    await failForTests(token);
                }

                if (sqlite)
                {
                    await EnsureSqliteOwnerIndexAsync(db, logger, token);
                    ensuredDatabaseKey = databaseKey;
                    return;
                }

                await db.Database.ExecuteSqlRawAsync(CreateSql, token);
                await db.Database.ExecuteSqlRawAsync(AddPhoneAndOrganizationSql, token);
                await db.Database.ExecuteSqlRawAsync(AddClaimSourceColumnsSql, token);
                try
                {
                    await db.Database.ExecuteSqlRawAsync(EnsureOwnerObjectIdUniqueIndexSql, token);
                }
                catch (Exception ex) when (IsDuplicateOwnerObjectIdBlock(ex))
                {
                    try
                    {
                        var groups = await ScalarIntAsync(db, DuplicateGroupCountSql, token);
                        logger.LogError(
                            "UserProfiles has {DuplicateGroupCount} duplicate OwnerObjectId groups; unique index not created. Resolve duplicates first.",
                            groups);
                    }
                    catch (Exception)
                    {
                        logger.LogError(
                            "UserProfiles has duplicate OwnerObjectId values; unique index not created. Resolve duplicates first. The duplicate-group count could not be read.");
                    }
                }

                ensuredDatabaseKey = databaseKey;
            }

            try
            {
                // One retry layer, still bounded by the caller's token (the
                // server-owned 30s budget on the request path). SQL Server retries
                // the whole batch and that suppresses the per-command retry.
                // SQLite does not retry, so the explicit loop is the only layer there.
                var strategy = db.Database.CreateExecutionStrategy();
                if (strategy.RetriesOnFailure)
                {
                    await strategy.ExecuteAsync(
                        async token => await ExecuteOnceAsync(token),
                        ct);
                }
                else if (circuit is not null)
                {
                    await ProfileStoreResilience.Pipeline.ExecuteAsync(
                        async token => await ExecuteOnceAsync(token),
                        ct);
                }
                else
                {
                    await ExecuteOnceAsync(ct);
                }

                circuit?.Close();
            }
            catch (Exception ex) when (circuit is not null && ex is not ProfileStoreUnavailableException)
            {
                // Transient retries are already exhausted. A non-transient error,
                // including cancellation, opens the same short break. Open before
                // releasing the gate so a waiter does not run the batch.
                circuit.Open(circuit.Now);
                throw;
            }

            return true;
        }
        finally
        {
            // A probe that did not close must not stay half-open. Half-open rejects
            // every later call, which would 503 the instance until restart.
            if (probing && circuit!.State == ProfileStoreCircuitState.HalfOpen)
            {
                circuit.Open(circuit.Now);
            }

            Gate.Release();
        }
    }

    /// <summary>
    /// SQLite :memory: uses one connection string for every connection, and each open
    /// connection is its own database. A SQL Server database is the server plus the
    /// catalog. SqlClient drops the password from <see cref="DbConnection.ConnectionString"/>
    /// after the connection has been opened when Persist Security Info is false, so the
    /// raw string is not a stable id and is never stored. A SQLite file is its full path.
    /// </summary>
    private static string DatabaseKey(PfohDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var connectionString = connection.ConnectionString ?? string.Empty;
        if (connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return MemoryDatabaseKeys.GetValue(connection, static _ => Guid.NewGuid().ToString("N"));
        }

        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return SqliteFileKey(connection);
        }

        return SqlServerKey(connectionString);
    }

    /// <summary>
    /// Server and database name only. The builder accepts either form of the connection
    /// string, with or without the password SqlClient removed.
    /// </summary>
    private static string SqlServerKey(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource.Trim().ToUpperInvariant();
        var catalog = builder.InitialCatalog.Trim().ToUpperInvariant();
        return string.Concat("sql:", server, "\n", catalog);
    }

    private static string SqliteFileKey(DbConnection connection)
    {
        var source = connection.DataSource?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            return "sqlite";
        }

        return "sqlite:" + Path.GetFullPath(source);
    }

    internal static void ResetForTests() => ensuredDatabaseKey = null;

    internal static bool IsDuplicateOwnerObjectIdBlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(DuplicateOwnerObjectIdIndexMessage, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task EnsureSqliteOwnerIndexAsync(PfohDbContext db, ILogger logger, CancellationToken ct)
    {
        var tables = await ScalarIntAsync(
            db,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UserProfiles'",
            ct);
        if (tables == 0)
        {
            return;
        }

        if (await SqliteHasOwnerUniqueIndexAsync(db, ct))
        {
            return;
        }

        var groups = await ScalarIntAsync(db, SqliteDuplicateGroupCountSql, ct);
        if (groups > 0)
        {
            logger.LogError(
                "UserProfiles has {DuplicateGroupCount} duplicate OwnerObjectId groups; unique index not created. Resolve duplicates first.",
                groups);
            return;
        }

        var notNull = await ScalarIntAsync(
            db,
            "SELECT \"notnull\" FROM pragma_table_info('UserProfiles') WHERE name = 'OwnerObjectId'",
            ct);
        var sql = notNull == 1
            ? "CREATE UNIQUE INDEX IF NOT EXISTS IX_UserProfiles_OwnerObjectId ON UserProfiles (OwnerObjectId)"
            : "CREATE UNIQUE INDEX IF NOT EXISTS IX_UserProfiles_OwnerObjectId ON UserProfiles (OwnerObjectId) WHERE OwnerObjectId IS NOT NULL";
        await ExecuteAsync(db, sql, ct);
    }

    private const string SqliteDuplicateGroupCountSql = """
        SELECT COUNT(*)
        FROM (
            SELECT OwnerObjectId
            FROM UserProfiles
            WHERE OwnerObjectId IS NOT NULL
            GROUP BY OwnerObjectId
            HAVING COUNT(*) > 1
        ) AS duplicate_groups
        """;

    private static async Task<bool> SqliteHasOwnerUniqueIndexAsync(PfohDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'UserProfiles'";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sql = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (sql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("OwnerObjectId", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<int> ScalarIntAsync(PfohDbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? 0 : Convert.ToInt32(value);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task ExecuteAsync(PfohDbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }
}
