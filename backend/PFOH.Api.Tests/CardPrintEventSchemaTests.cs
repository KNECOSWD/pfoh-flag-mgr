using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class CardPrintEventSchemaTests
{
    [Fact]
    public void Up_sql_creates_only_the_print_event_table_and_can_run_twice()
    {
        var sql = CardPrintEventSchema.CreateSql;
        Assert.Contains("IF OBJECT_ID(N'[dbo].[CardPrintEvents]', N'U') IS NULL", sql);
        Assert.Contains("CREATE TABLE [dbo].[CardPrintEvents]", sql);
        Assert.Contains("[CardPrintBatchId] uniqueidentifier NOT NULL", sql);
        Assert.Contains("[PrintedBy] nvarchar(255) NOT NULL", sql);
        Assert.Contains("[PrintedUtc] datetime2 NOT NULL", sql);
        Assert.Contains("[CardCount] int NOT NULL", sql);
        Assert.Contains("[Comment] nvarchar(500) NULL", sql);
        Assert.Contains("UQ_CardPrintEvents_CardPrintBatchId", sql);
        Assert.DoesNotContain("HonoreeChangeRequests", sql);
        Assert.DoesNotContain("ALTER TABLE", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
    }

    [Fact]
    public void Down_sql_drops_only_the_print_event_table()
    {
        var sql = CardPrintEventSchema.DropSql;
        Assert.Contains("IF OBJECT_ID(N'[dbo].[CardPrintEvents]', N'U') IS NOT NULL", sql);
        Assert.Contains("DROP TABLE [dbo].[CardPrintEvents]", sql);
        Assert.DoesNotContain("Honoree", sql);
        Assert.DoesNotContain("UserProfiles", sql);
        Assert.DoesNotContain("ALTER TABLE", sql);
    }
}
