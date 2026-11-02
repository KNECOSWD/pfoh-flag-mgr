using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.ReprintQueueEvents. Production applies the same SQL from
/// ReprintQueueEventSchema.EnsureAsync. Existing tables are not changed.
/// Rollback: ReprintQueueEventSchema.DropSql drops only this table.
/// </summary>
[Migration("20261101120100_AddReprintQueueEvents")]
public partial class AddReprintQueueEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(ReprintQueueEventSchema.CreateSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(ReprintQueueEventSchema.DropSql);
    }
}
