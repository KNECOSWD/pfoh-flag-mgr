using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.HonoreeMergeAudits. Production applies the same SQL from
/// HonoreeMergeAuditSchema.EnsureAsync. Existing tables are not changed.
/// </summary>
[Migration("20261021160000_AddHonoreeMergeAudits")]
public partial class AddHonoreeMergeAudits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(HonoreeMergeAuditSchema.CreateSql);
        migrationBuilder.Sql(HonoreeMergeAuditSchema.AddColumnSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(HonoreeMergeAuditSchema.DropSql);
    }
}
