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
    private static FlagSlotIndexEnsureResult cached;

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
        IF OBJECT_ID(N'[dbo].[Honorees]', N'U') IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM sys.indexes
            WHERE name = N'UX_Honorees_OneActiveFlagGrid'
              AND object_id = OBJECT_ID(N'[dbo].[Honorees]')
        )
        BEGIN
            DECLARE @DuplicateActiveFlags int = (
                SELECT COUNT(*)
                FROM (
                    SELECT [FlagGridId]
                    FROM [dbo].[Honorees]
                    WHERE [IsActive] = 1
                      AND [DeletedDate] IS NULL
                      AND [FlagGridId] IS NOT NULL
                    GROUP BY [FlagGridId]
                    HAVING COUNT(*) > 1
                ) AS [DuplicateFlags]
            );
            IF @DuplicateActiveFlags > 0
            BEGIN
                DECLARE @FlagSlotIndexWarning nvarchar(300) =
                    N'UX_Honorees_OneActiveFlagGrid was not created because '
                    + CONVERT(nvarchar(20), @DuplicateActiveFlags)
                    + N' flag positions already have more than one active honoree.';
                PRINT @FlagSlotIndexWarning;
            END
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

    public static async Task<FlagSlotIndexEnsureResult> EnsureAsync(PfohDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            return default;
        }

        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        if (ready)
        {
            return cached;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (ready)
            {
                return cached;
            }

            await db.Database.ExecuteSqlRawAsync(SchemaSql.ForExecuteSqlRaw(EnsureSql), ct);
            cached = await ReadResultAsync(db, ct);
            ready = true;
            return cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Warning text when the filtered index was left off because active honorees
    /// already share flags. Null when the index is present or there is nothing to skip.
    /// </summary>
    internal static string? SkipWarning(int duplicateFlagCount, bool indexPresent) =>
        duplicateFlagCount > 0 && !indexPresent
            ? $"UX_Honorees_OneActiveFlagGrid was not created because {duplicateFlagCount} flag positions already have more than one active honoree."
            : null;

    private static async Task<FlagSlotIndexEnsureResult> ReadResultAsync(PfohDbContext db, CancellationToken ct)
    {
        var duplicates = await db.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*) AS [Value]
            FROM (
                SELECT [FlagGridId]
                FROM [dbo].[Honorees]
                WHERE [IsActive] = 1
                  AND [DeletedDate] IS NULL
                  AND [FlagGridId] IS NOT NULL
                GROUP BY [FlagGridId]
                HAVING COUNT(*) > 1
            ) AS [DuplicateFlags]
            """).SingleAsync(ct);
        var indexes = await db.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*) AS [Value]
            FROM sys.indexes
            WHERE name = N'UX_Honorees_OneActiveFlagGrid'
              AND object_id = OBJECT_ID(N'dbo.Honorees')
            """).SingleAsync(ct);
        return new FlagSlotIndexEnsureResult(duplicates, indexes > 0);
    }
}

public readonly record struct FlagSlotIndexEnsureResult(int DuplicateFlagCount, bool IndexPresent)
{
    public string? SkipWarning => HonoreeActiveFlagSlotSchema.SkipWarning(DuplicateFlagCount, IndexPresent);
}
