using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.HonoreeClaims and backfills one row per existing person claim.
/// FlagClaims columns stay. The active flag-grid index becomes unique per
/// grid and person so more than one person can claim the same honoree.
/// Production applies the same SQL from HonoreeClaimSchema.EnsureAsync.
/// </summary>
[Migration("20261021120000_AddHonoreeClaims")]
public partial class AddHonoreeClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(HonoreeClaimSchema.CreateSql);
        migrationBuilder.Sql(HonoreeClaimSchema.BackfillSql);
        migrationBuilder.Sql(PendingSubmissionGridSchema.ActiveFlagGridIndexSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(HonoreeClaimSchema.DropSql);
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'dbo.FlagClaims', N'U') IS NOT NULL
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.FlagClaims')
                      AND name = N'UX_FlagClaims_ActiveFlagGrid')
                    DROP INDEX [UX_FlagClaims_ActiveFlagGrid] ON [dbo].[FlagClaims];

                CREATE UNIQUE INDEX [UX_FlagClaims_ActiveFlagGrid]
                    ON [dbo].[FlagClaims]([FlagGridId])
                    WHERE [ClaimStatus] IN (N'Claimed', N'Submitted', N'Approved')
                      AND [FlagGridId] IS NOT NULL;
            END
            """);
    }
}
