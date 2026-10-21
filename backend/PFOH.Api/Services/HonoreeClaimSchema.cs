using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.HonoreeClaims and copies one row per existing person-and-honoree
/// claim. Production has no EF migration history, so startup runs this SQL.
/// FlagClaims columns are not dropped and are not altered here.
/// </summary>
public static class HonoreeClaimSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeClaims]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[HonoreeClaims] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_HonoreeClaims] PRIMARY KEY,
                [HonoreeId] int NOT NULL,
                [UserId] nvarchar(128) NOT NULL,
                [ClaimedUtc] datetime2 NOT NULL,
                [Email] nvarchar(255) NOT NULL CONSTRAINT [DF_HonoreeClaims_Email] DEFAULT (N''),
                [DisplayName] nvarchar(255) NULL,
                [ClaimStatus] nvarchar(30) NOT NULL,
                [SourceFlagClaimId] int NULL,
                CONSTRAINT [FK_HonoreeClaims_Honorees_HonoreeId] FOREIGN KEY ([HonoreeId]) REFERENCES [dbo].[Honorees] ([Id]),
                CONSTRAINT [UQ_HonoreeClaims_Honoree_User] UNIQUE ([HonoreeId], [UserId])
            );
            CREATE INDEX [IX_HonoreeClaims_UserId] ON [dbo].[HonoreeClaims] ([UserId]);
        END
        """;

    internal const string BackfillSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeClaims]', N'U') IS NOT NULL
           AND OBJECT_ID(N'[dbo].[FlagClaims]', N'U') IS NOT NULL
        BEGIN
            INSERT INTO [dbo].[HonoreeClaims] (
                [HonoreeId], [UserId], [ClaimedUtc], [Email], [DisplayName], [ClaimStatus], [SourceFlagClaimId])
            SELECT
                c.[HonoreeId],
                c.[ExternalUserObjectId],
                c.[CreatedUtc],
                c.[ExternalUserEmail],
                c.[ExternalUserName],
                c.[ClaimStatus],
                c.[Id]
            FROM [dbo].[FlagClaims] c
            INNER JOIN (
                SELECT [HonoreeId], [ExternalUserObjectId], MIN([Id]) AS [KeepId]
                FROM [dbo].[FlagClaims]
                WHERE [HonoreeId] IS NOT NULL
                  AND [ExternalUserObjectId] NOT LIKE N'admin-reprint:%'
                  AND [ClaimStatus] NOT IN (N'Unclaimed', N'Cancelled', N'AdminDirectEditCompleted')
                GROUP BY [HonoreeId], [ExternalUserObjectId]
            ) picked ON picked.[KeepId] = c.[Id]
            WHERE NOT EXISTS (
                SELECT 1
                FROM [dbo].[HonoreeClaims] existing
                WHERE existing.[HonoreeId] = c.[HonoreeId]
                  AND existing.[UserId] = c.[ExternalUserObjectId]);
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeClaims]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[HonoreeClaims];
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
            await db.Database.ExecuteSqlRawAsync(BackfillSql, ct);
            await db.Database.ExecuteSqlRawAsync(PendingSubmissionGridSchema.ActiveFlagGridIndexSql, ct);
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
