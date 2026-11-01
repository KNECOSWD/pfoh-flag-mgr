using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.ReprintQueueEvents. The create is skipped when the table
/// already exists. No other table is altered. Rollback drops only this table:
/// DROP TABLE [dbo].[ReprintQueueEvents];
/// </summary>
public static class ReprintQueueEventSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[ReprintQueueEvents]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ReprintQueueEvents] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ReprintQueueEvents] PRIMARY KEY,
                [ChangeRequestId] int NOT NULL,
                [HonoreeId] int NULL,
                [Action] nvarchar(20) NOT NULL,
                [ActorEmail] nvarchar(256) NOT NULL,
                [ActorObjectId] nvarchar(128) NULL,
                [Utc] datetime2 NOT NULL,
                [Reason] nvarchar(1000) NULL,
                [Source] nvarchar(20) NOT NULL,
                [BatchSize] int NOT NULL
            );
            CREATE INDEX [IX_ReprintQueueEvents_ChangeRequestId]
                ON [dbo].[ReprintQueueEvents] ([ChangeRequestId], [Utc]);
            CREATE INDEX [IX_ReprintQueueEvents_Utc]
                ON [dbo].[ReprintQueueEvents] ([Utc]);
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[ReprintQueueEvents]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[ReprintQueueEvents];
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
