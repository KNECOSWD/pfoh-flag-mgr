using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.FlagGridRemovalAudits. The create is skipped when the table
/// already exists. An existing FlagGridId column is made nullable so a
/// reactivation with no flag can still be audited. No row is updated or deleted.
/// Rollback drops only this table:
/// DROP TABLE [dbo].[FlagGridRemovalAudits];
/// </summary>
public static class FlagGridRemovalAuditSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[FlagGridRemovalAudits]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[FlagGridRemovalAudits] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_FlagGridRemovalAudits] PRIMARY KEY,
                [HonoreeId] int NOT NULL,
                [FlagGridId] int NULL,
                [RemovalQueueItemId] int NULL,
                [Action] nvarchar(20) NOT NULL,
                [ActorEmail] nvarchar(256) NOT NULL,
                [ActorObjectId] nvarchar(128) NULL,
                [OccurredUtc] datetime2 NOT NULL,
                [Reason] nvarchar(1000) NOT NULL,
                [BeforeState] nvarchar(max) NOT NULL,
                [AfterState] nvarchar(max) NOT NULL,
                [PdfWarning] nvarchar(500) NULL
            );
            CREATE INDEX [IX_FlagGridRemovalAudits_HonoreeId]
                ON [dbo].[FlagGridRemovalAudits] ([HonoreeId], [OccurredUtc]);
            CREATE INDEX [IX_FlagGridRemovalAudits_FlagGridId]
                ON [dbo].[FlagGridRemovalAudits] ([FlagGridId], [OccurredUtc]);
        END
        """;

    internal const string AlterFlagGridIdNullableSql = """
        IF OBJECT_ID(N'[dbo].[FlagGridRemovalAudits]', N'U') IS NOT NULL
        AND EXISTS (
            SELECT 1
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'[dbo].[FlagGridRemovalAudits]')
              AND name = N'FlagGridId'
              AND is_nullable = 0
        )
        BEGIN
            ALTER TABLE [dbo].[FlagGridRemovalAudits] ALTER COLUMN [FlagGridId] int NULL;
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[FlagGridRemovalAudits]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[FlagGridRemovalAudits];
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
            await db.Database.ExecuteSqlRawAsync(SchemaSql.ForExecuteSqlRaw(AlterFlagGridIdNullableSql), ct);
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
