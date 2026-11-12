using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Schema only. Makes FlagGridRemovalAudits.FlagGridId nullable so a restore
/// into the approval queue can be audited with no flag. No row is updated or
/// deleted. Production applies the same SQL from FlagGridRemovalAuditSchema.EnsureAsync
/// because the live database has no EF migration history.
/// Down leaves FlagGridId nullable. Forcing NOT NULL would fail after a null was stored.
/// </summary>
[Migration("20261111120000_InactiveHonoreeReactivationGuards")]
public partial class InactiveHonoreeReactivationGuards : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FlagGridRemovalAuditSchema.CreateSql);
        migrationBuilder.Sql(FlagGridRemovalAuditSchema.AlterFlagGridIdNullableSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRINT N'FlagGridId stays nullable. Rollback does not rewrite rows.';");
    }
}
