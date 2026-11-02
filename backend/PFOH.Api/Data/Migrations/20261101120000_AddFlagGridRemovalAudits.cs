using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.FlagGridRemovalAudits. Production applies the same SQL from
/// FlagGridRemovalAuditSchema.EnsureAsync. Existing tables are not changed.
/// Rollback: FlagGridRemovalAuditSchema.DropSql drops only this table.
/// </summary>
[Migration("20261101120000_AddFlagGridRemovalAudits")]
public partial class AddFlagGridRemovalAudits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FlagGridRemovalAuditSchema.CreateSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FlagGridRemovalAuditSchema.DropSql);
    }
}
