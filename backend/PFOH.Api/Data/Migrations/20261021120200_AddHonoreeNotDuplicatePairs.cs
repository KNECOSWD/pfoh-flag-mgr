using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.HonoreeNotDuplicatePairs. Production applies the same SQL from
/// HonoreeNotDuplicatePairSchema.EnsureAsync.
/// </summary>
[Migration("20261021120200_AddHonoreeNotDuplicatePairs")]
public partial class AddHonoreeNotDuplicatePairs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(HonoreeNotDuplicatePairSchema.CreateSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(HonoreeNotDuplicatePairSchema.DropSql);
    }
}
