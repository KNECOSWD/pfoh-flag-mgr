using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds Phone and Organization to dbo.UserProfiles.
/// This migration does not create or alter honoree, sponsor, or flag tables.
/// Production applies the same SQL from <see cref="UserProfileSchema.EnsureAsync"/>
/// because that database has no EF migration history.
/// </summary>
[Migration("20261010000000_AddUserProfilePhoneAndOrganization")]
public partial class AddUserProfilePhoneAndOrganization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(UserProfileSchema.CreateSql);
        migrationBuilder.Sql(UserProfileSchema.AddPhoneAndOrganizationSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(UserProfileSchema.DropPhoneAndOrganizationSql);
    }
}
