using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Schema only. Makes FlagGridRemovalAudits.FlagGridId nullable and adds
/// UX_Honorees_OneActiveFlagGrid when no two active honorees already share a flag.
/// No row is updated or deleted. Production runs the same SQL from
/// FlagGridRemovalAuditSchema.EnsureAsync and HonoreeActiveFlagSlotSchema.EnsureAsync
/// because the live database has no EF migration history.
/// Rollback drops the unique index and leaves FlagGridId nullable.
/// </summary>
[Migration("20261111120000_InactiveHonoreeReactivationGuards")]
public partial class InactiveHonoreeReactivationGuards : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FlagGridRemovalAuditSchema.CreateSql);
        migrationBuilder.Sql(FlagGridRemovalAuditSchema.AlterFlagGridIdNullableSql);
        migrationBuilder.Sql(HonoreeActiveFlagSlotSchema.EnsureSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(HonoreeActiveFlagSlotSchema.DropSql);
    }
}
