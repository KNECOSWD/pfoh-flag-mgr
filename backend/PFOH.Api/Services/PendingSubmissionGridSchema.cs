using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Lets a pending new submission exist without a flag grid.
/// Production has no EF migration history, so this alters the two claim
/// columns in place and does not rebuild the honoree tables.
/// </summary>
public static class PendingSubmissionGridSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string MakeFlagGridOptionalSql = """
        IF OBJECT_ID(N'dbo.FlagClaims', N'U') IS NOT NULL
           AND COLUMNPROPERTY(OBJECT_ID(N'dbo.FlagClaims'), N'FlagGridId', 'AllowsNull') = 0
        BEGIN
            DECLARE @dropClaimFk nvarchar(max) = N'';
            SELECT @dropClaimFk = @dropClaimFk + N'ALTER TABLE [dbo].[FlagClaims] DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            WHERE fk.parent_object_id = OBJECT_ID(N'dbo.FlagClaims')
              AND c.name = N'FlagGridId';
            IF LEN(@dropClaimFk) > 0 EXEC sp_executesql @dropClaimFk;

            ALTER TABLE [dbo].[FlagClaims] ALTER COLUMN [FlagGridId] int NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_FlagClaims_FlagGrids_FlagGridId')
                ALTER TABLE [dbo].[FlagClaims] ADD CONSTRAINT [FK_FlagClaims_FlagGrids_FlagGridId]
                    FOREIGN KEY ([FlagGridId]) REFERENCES [dbo].[FlagGrids] ([Id]);
        END

        IF OBJECT_ID(N'dbo.HonoreeChangeRequests', N'U') IS NOT NULL
           AND COLUMNPROPERTY(OBJECT_ID(N'dbo.HonoreeChangeRequests'), N'FlagGridId', 'AllowsNull') = 0
        BEGIN
            DECLARE @dropRequestFk nvarchar(max) = N'';
            SELECT @dropRequestFk = @dropRequestFk + N'ALTER TABLE [dbo].[HonoreeChangeRequests] DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            WHERE fk.parent_object_id = OBJECT_ID(N'dbo.HonoreeChangeRequests')
              AND c.name = N'FlagGridId';
            IF LEN(@dropRequestFk) > 0 EXEC sp_executesql @dropRequestFk;

            ALTER TABLE [dbo].[HonoreeChangeRequests] ALTER COLUMN [FlagGridId] int NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_HonoreeChangeRequests_FlagGrids_FlagGridId')
                ALTER TABLE [dbo].[HonoreeChangeRequests] ADD CONSTRAINT [FK_HonoreeChangeRequests_FlagGrids_FlagGridId]
                    FOREIGN KEY ([FlagGridId]) REFERENCES [dbo].[FlagGrids] ([Id]);
        END
        """;

    internal const string ActiveFlagGridIndexSql = """
        IF OBJECT_ID(N'dbo.FlagClaims', N'U') IS NOT NULL
        BEGIN
            DECLARE @activeFlagGridCols nvarchar(max) = NULL;
            SELECT @activeFlagGridCols = STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'dbo.FlagClaims')
              AND i.name = N'UX_FlagClaims_ActiveFlagGrid'
              AND ic.is_included_column = 0;

            IF @activeFlagGridCols IS NULL OR @activeFlagGridCols <> N'FlagGridId,ExternalUserObjectId'
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.FlagClaims')
                      AND name = N'UX_FlagClaims_ActiveFlagGrid')
                    DROP INDEX [UX_FlagClaims_ActiveFlagGrid] ON [dbo].[FlagClaims];

                CREATE UNIQUE INDEX [UX_FlagClaims_ActiveFlagGrid]
                    ON [dbo].[FlagClaims]([FlagGridId], [ExternalUserObjectId])
                    WHERE [ClaimStatus] IN (N'Claimed', N'Submitted', N'Approved')
                      AND [FlagGridId] IS NOT NULL;
            END
        END
        """;

    /// <summary>
    /// Nullable client id for one nominate request, plus a unique index that
    /// ignores nulls. Existing rows stay null. Nothing is backfilled.
    /// </summary>
    internal const string AddClientRequestIdSql = """
        IF OBJECT_ID(N'dbo.FlagClaims', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.FlagClaims', N'ClientRequestId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[FlagClaims] ADD [ClientRequestId] uniqueidentifier NULL;
        END

        IF OBJECT_ID(N'dbo.FlagClaims', N'U') IS NOT NULL
           AND NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.FlagClaims')
                  AND name = N'UX_FlagClaims_ClientRequestId')
        BEGIN
            CREATE UNIQUE INDEX [UX_FlagClaims_ClientRequestId]
                ON [dbo].[FlagClaims]([ClientRequestId])
                WHERE [ClientRequestId] IS NOT NULL;
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

            await db.Database.ExecuteSqlRawAsync(MakeFlagGridOptionalSql, ct);
            await db.Database.ExecuteSqlRawAsync(ActiveFlagGridIndexSql, ct);
            await db.Database.ExecuteSqlRawAsync(AddClientRequestIdSql, ct);
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
