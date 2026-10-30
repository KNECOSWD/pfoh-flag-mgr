using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds reactivation tokens and the review-queue rows that record a used link.
/// Production applies the same SQL from ReactivationSchema.EnsureAsync.
/// </summary>
[Migration("20261030180000_AddHonoreeReactivation")]
public partial class AddHonoreeReactivation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(ReactivationSchema.CreateSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(ReactivationSchema.DropSql);
    }
}
