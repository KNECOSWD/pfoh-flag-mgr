using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.UserProfiles when it is missing.
/// Production already has the honoree tables and no EF migration history, so a
/// full migration would try to recreate those tables. This statement only adds
/// the profile table.
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
                [CreatedUtc] datetime2 NOT NULL,
                [UpdatedUtc] datetime2 NOT NULL,
                CONSTRAINT [UQ_UserProfiles_OwnerObjectId] UNIQUE ([OwnerObjectId])
            );
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
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
