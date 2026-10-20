using Microsoft.EntityFrameworkCore.Migrations;
using PFOH.Api.Services;

#nullable disable

namespace PFOH.Api.Data.Migrations;

/// <summary>
/// Adds dbo.CardPrintEvents, one row per Mark printed batch.
/// This migration does not create or alter honoree, claim, or profile tables.
/// Production applies the same SQL from <see cref="CardPrintEventSchema.EnsureAsync"/>
/// because that database has no EF migration history.
/// </summary>
[Migration("20261020180000_AddCardPrintEvents")]
public partial class AddCardPrintEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(CardPrintEventSchema.CreateSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(CardPrintEventSchema.DropSql);
    }
}
