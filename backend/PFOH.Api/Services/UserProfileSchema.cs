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
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
