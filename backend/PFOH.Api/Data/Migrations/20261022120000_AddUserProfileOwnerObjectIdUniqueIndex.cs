using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Schema only. Adds nullable claim/profile source columns and a unique index
/// on OwnerObjectId when that column is not already unique. No data changes.
/// Production also runs this SQL from <see cref="UserProfileSchema.EnsureAsync"/>
/// because that database has no EF migration history.
/// </summary>
[Migration("20261022120000_AddUserProfileOwnerObjectIdUniqueIndex")]
public partial class AddUserProfileOwnerObjectIdUniqueIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(UserProfileSchema.AddClaimSourceColumnsSql);
        migrationBuilder.Sql(UserProfileSchema.EnsureOwnerObjectIdUniqueIndexSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(UserProfileSchema.DropOwnerObjectIdUniqueIndexSql);
        migrationBuilder.Sql(UserProfileSchema.DropClaimSourceColumnsSql);
    }
}
