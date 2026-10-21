using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.ReprintDuplicateReviews. Reprint queue columns are not altered.
/// Production applies the same SQL from ReprintDuplicateReviewSchema.EnsureAsync.
/// </summary>
[Migration("20261021120100_AddReprintDuplicateReviews")]
public partial class AddReprintDuplicateReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(ReprintDuplicateReviewSchema.CreateSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(ReprintDuplicateReviewSchema.DropSql);
    }
}
