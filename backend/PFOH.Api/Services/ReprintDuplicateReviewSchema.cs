using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.ReprintDuplicateReviews. Existing reprint and honoree columns
/// are not altered. Production applies this from startup because that database
/// has no EF migration history.
/// </summary>
public static class ReprintDuplicateReviewSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[ReprintDuplicateReviews]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ReprintDuplicateReviews] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ReprintDuplicateReviews] PRIMARY KEY,
                [HonoreeId] int NOT NULL,
                [DuplicateChangeRequestId] int NOT NULL,
                [KeptChangeRequestId] int NOT NULL,
                [Status] nvarchar(20) NOT NULL,
                [FlaggedUtc] datetime2 NOT NULL,
                [ResolvedUtc] datetime2 NULL,
                [ResolvedBy] nvarchar(255) NULL,
                CONSTRAINT [FK_ReprintDuplicateReviews_Honorees_HonoreeId] FOREIGN KEY ([HonoreeId]) REFERENCES [dbo].[Honorees] ([Id]),
                CONSTRAINT [FK_ReprintDuplicateReviews_DuplicateChangeRequest] FOREIGN KEY ([DuplicateChangeRequestId]) REFERENCES [dbo].[HonoreeChangeRequests] ([Id]),
                CONSTRAINT [FK_ReprintDuplicateReviews_KeptChangeRequest] FOREIGN KEY ([KeptChangeRequestId]) REFERENCES [dbo].[HonoreeChangeRequests] ([Id])
            );
            CREATE UNIQUE INDEX [UX_ReprintDuplicateReviews_Duplicate]
                ON [dbo].[ReprintDuplicateReviews] ([DuplicateChangeRequestId]);
            CREATE INDEX [IX_ReprintDuplicateReviews_Status] ON [dbo].[ReprintDuplicateReviews] ([Status]);
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[ReprintDuplicateReviews]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[ReprintDuplicateReviews];
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
