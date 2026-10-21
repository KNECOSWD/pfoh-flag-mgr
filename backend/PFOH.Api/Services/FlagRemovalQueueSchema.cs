using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.FlagRemovalQueueItems. The create is skipped when the table
/// already exists. No other table or column is altered or dropped.
/// No database trigger. A grid change is recorded only when
/// application code calls <see cref="FlagRemovalQueue.Record"/>.
/// </summary>
public static class FlagRemovalQueueSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[FlagRemovalQueueItems]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[FlagRemovalQueueItems] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_FlagRemovalQueueItems] PRIMARY KEY,
                [HonoreeId] int NULL,
                [HonoreeName] nvarchar(200) NOT NULL,
                [FlagGridId] int NOT NULL,
                [FlagGridName] nvarchar(50) NOT NULL,
                [NewFlagGridId] int NULL,
                [NewFlagGridName] nvarchar(50) NULL,
                [Source] nvarchar(20) NOT NULL,
                [SourceDetail] nvarchar(200) NULL,
                [MergeAuditId] int NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [CreatedBy] nvarchar(256) NOT NULL,
                [DoneUtc] datetime2 NULL,
                [DoneBy] nvarchar(256) NULL,
                [CanceledUtc] datetime2 NULL,
                [CanceledBy] nvarchar(256) NULL,
                [CancelReason] nvarchar(200) NULL,
                CONSTRAINT [FK_FlagRemovalQueueItems_HonoreeMergeAudits_MergeAuditId]
                    FOREIGN KEY ([MergeAuditId]) REFERENCES [dbo].[HonoreeMergeAudits] ([Id])
            );
            CREATE INDEX [IX_FlagRemovalQueueItems_Open]
                ON [dbo].[FlagRemovalQueueItems] ([FlagGridName], [Id])
                WHERE [DoneUtc] IS NULL AND [CanceledUtc] IS NULL;
            CREATE INDEX [IX_FlagRemovalQueueItems_MergeAuditId]
                ON [dbo].[FlagRemovalQueueItems] ([MergeAuditId]);
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[FlagRemovalQueueItems]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[FlagRemovalQueueItems];
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

            await db.Database.ExecuteSqlRawAsync(SchemaSql.ForExecuteSqlRaw(CreateSql), ct);
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
