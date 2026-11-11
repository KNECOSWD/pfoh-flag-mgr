using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Adds a filtered unique index so two active honorees cannot share a flag.
/// The index is skipped when that data already exists. No row is updated or deleted.
/// Rollback: <see cref="DropSql"/>.
/// </summary>
public static class HonoreeActiveFlagSlotSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string EnsureSql = """
        IF OBJECT_ID(N'[dbo].[Honorees]', N'U') IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM sys.indexes
            WHERE name = N'UX_Honorees_OneActiveFlagGrid'
              AND object_id = OBJECT_ID(N'[dbo].[Honorees]')
        )
        AND NOT EXISTS (
            SELECT 1
            FROM [dbo].[Honorees]
            WHERE [IsActive] = 1
              AND [DeletedDate] IS NULL
              AND [FlagGridId] IS NOT NULL
            GROUP BY [FlagGridId]
            HAVING COUNT(*) > 1
        )
        BEGIN
            CREATE UNIQUE INDEX [UX_Honorees_OneActiveFlagGrid]
                ON [dbo].[Honorees] ([FlagGridId])
                WHERE [IsActive] = 1 AND [DeletedDate] IS NULL AND [FlagGridId] IS NOT NULL;
        END
        """;

    internal const string DropSql = """
        IF EXISTS (
            SELECT 1
            FROM sys.indexes
            WHERE name = N'UX_Honorees_OneActiveFlagGrid'
              AND object_id = OBJECT_ID(N'[dbo].[Honorees]')
        )
        BEGIN
            DROP INDEX [UX_Honorees_OneActiveFlagGrid] ON [dbo].[Honorees];
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

            await db.Database.ExecuteSqlRawAsync(SchemaSql.ForExecuteSqlRaw(EnsureSql), ct);
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
