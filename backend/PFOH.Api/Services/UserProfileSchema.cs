using Microsoft.EntityFrameworkCore;
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
    /// Schema only. Creates the unique index when the table has none on OwnerObjectId.
    /// Does not update, delete, or insert rows.
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
            CREATE UNIQUE INDEX [IX_UserProfiles_OwnerObjectId]
                ON [dbo].[UserProfiles] ([OwnerObjectId]);
        END
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

    public static async Task EnsureAsync(PfohDbContext db, CancellationToken ct)
    {
        if (ready || !db.Database.IsRelational())
        {
            return;
        }

        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
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

            await db.Database.ExecuteSqlRawAsync(CreateSql, ct);
            await db.Database.ExecuteSqlRawAsync(AddPhoneAndOrganizationSql, ct);
            await db.Database.ExecuteSqlRawAsync(AddClaimSourceColumnsSql, ct);
            await db.Database.ExecuteSqlRawAsync(EnsureOwnerObjectIdUniqueIndexSql, ct);
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
