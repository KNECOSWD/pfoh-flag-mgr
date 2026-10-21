using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.HonoreeMergeAudits. The create is skipped when the table already
/// exists. No existing table or column is altered or dropped.
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
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
