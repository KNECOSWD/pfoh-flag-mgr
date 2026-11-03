using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.SubmissionReceipts, dbo.SubmissionAuditEvents, and the nullable
/// CorrectsChangeRequestId column. Production applies the same SQL from
/// SubmissionGuardSchema.EnsureAsync. Existing rows are not rewritten.
/// Rollback: SubmissionGuardSchema.DropSql drops only these additions.
/// Same-day Superseded rows are a status value that already existed. Restore
/// them from dbo.SubmissionAuditEvents before running the drop, or leave them
/// Superseded. Do not drop the column while the app still reads it.
/// </summary>
[Migration("20261103180000_AddSubmissionGuard")]
public partial class AddSubmissionGuard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SubmissionGuardSchema.CreateSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SubmissionGuardSchema.DropSql);
    }
}
