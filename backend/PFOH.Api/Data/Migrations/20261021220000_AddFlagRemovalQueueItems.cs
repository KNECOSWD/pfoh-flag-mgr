using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.FlagRemovalQueueItems. Production applies the same SQL from
/// FlagRemovalQueueSchema.EnsureAsync. Existing tables are not changed.
/// </summary>
[Migration("20261021220000_AddFlagRemovalQueueItems")]
public partial class AddFlagRemovalQueueItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FlagRemovalQueueSchema.CreateSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FlagRemovalQueueSchema.DropSql);
    }
}
