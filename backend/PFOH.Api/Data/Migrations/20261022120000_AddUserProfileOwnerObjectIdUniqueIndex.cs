using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Schema only. Adds nullable claim/profile source columns and a unique index
/// on OwnerObjectId when that column is not already unique and no non-null
/// object id is repeated. Duplicate ids throw and roll this migration back.
/// No row is updated or deleted. Production runs the same SQL from
/// <see cref="UserProfileSchema.EnsureAsync"/> at startup, because the live
/// database has no EF migration history and the deploy workflow does not
/// apply migrations. Startup catches that duplicate failure, logs the group
/// count, and keeps serving.
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
