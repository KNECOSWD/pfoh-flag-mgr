using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.HonoreeMergeAudits. The create is skipped when the table already
/// exists. A later column on this table is added only when it is missing.
/// No other table or column is altered or dropped.
/// </summary>
public static class HonoreeMergeAuditSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeMergeAudits]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[HonoreeMergeAudits] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_HonoreeMergeAudits] PRIMARY KEY,
                [SurvivorId] int NOT NULL,
                [AbsorbedHonoreeIds] nvarchar(max) NOT NULL,
                [BeforeState] nvarchar(max) NOT NULL,
                [AfterSurvivorState] nvarchar(max) NOT NULL,
                [AfterState] nvarchar(max) NOT NULL CONSTRAINT [DF_HonoreeMergeAudits_AfterState] DEFAULT N'{}',
                [CreatedHonoreeClaimIds] nvarchar(max) NOT NULL,
                [MergedBy] nvarchar(255) NOT NULL,
                [MergedUtc] datetime2 NOT NULL,
                [SurvivorModifiedUtc] datetime2 NOT NULL,
                [UndoneBy] nvarchar(255) NULL,
                [UndoneUtc] datetime2 NULL
            );
            CREATE INDEX [IX_HonoreeMergeAudits_MergedUtc] ON [dbo].[HonoreeMergeAudits] ([MergedUtc]);
        END
        """;

    /// <summary>
    /// Adds <c>AfterState</c> when a previous start already created the table
    /// without that column. No other table is changed.
    /// </summary>
    internal const string AddColumnSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeMergeAudits]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.HonoreeMergeAudits', N'AfterState') IS NULL
        BEGIN
            ALTER TABLE [dbo].[HonoreeMergeAudits]
                ADD [AfterState] nvarchar(max) NOT NULL
                    CONSTRAINT [DF_HonoreeMergeAudits_AfterState] DEFAULT N'{}';
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeMergeAudits]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[HonoreeMergeAudits];
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
            await db.Database.ExecuteSqlRawAsync(AddColumnSql, ct);
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
