using System.Data;
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
    private static bool ready;

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
                FROM dbo.UserProfiles
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

    public static async Task EnsureAsync(PfohDbContext db, ILogger logger, CancellationToken ct)
    {
        if (ready || !db.Database.IsRelational())
        {
            return;
        }

        var provider = db.Database.ProviderName ?? string.Empty;
        var sqlServer = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        var sqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (!sqlServer && !sqlite)
        {
            return;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (ready)
            {
                return;
            }

            if (sqlite)
            {
                await EnsureSqliteOwnerIndexAsync(db, logger, ct);
                ready = true;
                return;
            }

            await db.Database.ExecuteSqlRawAsync(CreateSql, ct);
            await db.Database.ExecuteSqlRawAsync(AddPhoneAndOrganizationSql, ct);
            await db.Database.ExecuteSqlRawAsync(AddClaimSourceColumnsSql, ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync(EnsureOwnerObjectIdUniqueIndexSql, ct);
            }
            catch (Exception ex) when (IsDuplicateOwnerObjectIdBlock(ex))
            {
                try
                {
                    var groups = await ScalarIntAsync(db, DuplicateGroupCountSql, ct);
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

            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static void ResetForTests() => ready = false;

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
