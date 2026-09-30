using System.IO.Compression;
using System.Xml.Linq;
using ClosedXML.Excel;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class FlagMapExcelExporterTests
{
    [Fact]
    public void SelectRows_sorts_by_grid_and_skips_empty_honorees()
    {
        var rows = FlagMapExcelExporter.SelectRows(
        [
            new FlagMapExportSource("B-01", "B", 1, "Bravo Person"),
            new FlagMapExportSource("A-10", "A", 10, "Ten Person"),
            new FlagMapExportSource("A-2", "A", 2, "Two Person"),
            new FlagMapExportSource("C-01", "C", 1, "   "),
            new FlagMapExportSource("A-01", "A", 1, " One Person "),
            new FlagMapExportSource("D-01", "D", 1, "Zoe Adams"),
            new FlagMapExportSource("D-01", "D", 1, "Ann Zulu")
        ]);

        Assert.Equal(
            ["A-01", "A-2", "A-10", "B-01", "D-01", "D-01"],
            rows.Select(row => row.FlagGridName).ToArray());
        Assert.Equal("One", rows[0].FirstName);
        Assert.Equal("Person", rows[0].LastName);
        Assert.Equal("Two", rows[1].FirstName);
        Assert.Equal("Person", rows[1].LastName);
        Assert.Equal(["Ann", "Zoe"], rows.Skip(4).Select(row => row.FirstName).ToArray());
        Assert.Equal(["Zulu", "Adams"], rows.Skip(4).Select(row => row.LastName).ToArray());
    }

    [Theory]
    [InlineData("John Smith", "John", "Smith")]
    [InlineData("Mary Ann Smith", "Mary Ann", "Smith")]
    [InlineData("Robert Jones Jr.", "Robert", "Jones Jr.")]
    [InlineData("Robert Jones Jr", "Robert", "Jones Jr")]
    [InlineData("Robert Jones SR.", "Robert", "Jones SR.")]
    [InlineData("Robert Jones Sr", "Robert", "Jones Sr")]
    [InlineData("Anna Lee II", "Anna", "Lee II")]
    [InlineData("Anna Lee III", "Anna", "Lee III")]
    [InlineData("Anna Lee IV", "Anna", "Lee IV")]
    [InlineData("Mary Ann Jones III", "Mary Ann", "Jones III")]
    [InlineData("John A. Smith", "John A.", "Smith")]
    [InlineData("John II Smith", "John II", "Smith")]
    [InlineData("Robert Jones Jr. (Bobby)", "Robert", "Jones Jr. (Bobby)")]
    [InlineData("Mary Ann Smith (Mae)", "Mary Ann", "Smith (Mae)")]
    [InlineData("Madonna", "", "Madonna")]
    [InlineData("Jr.", "", "Jr.")]
    [InlineData("Robert Jr.", "", "Robert Jr.")]
    [InlineData("(Nick)", "", "(Nick)")]
    [InlineData("", "", "")]
    [InlineData("   ", "", "")]
    [InlineData(null, "", "")]
    [InlineData("  John   Smith  ", "John", "Smith")]
    public void SplitHonoreeName_splits_given_names_from_surname_and_suffix(
        string? honoreeName,
        string expectedFirst,
        string expectedLast)
    {
        var (firstName, lastName) = FlagMapExcelExporter.SplitHonoreeName(honoreeName);

        Assert.Equal(expectedFirst, firstName);
        Assert.Equal(expectedLast, lastName);
    }

    [Fact]
    public void Build_writes_an_excel_table_with_autofilter()
    {
        var rows = FlagMapExcelExporter.SelectRows(
        [
            new FlagMapExportSource("A-10", "A", 10, "Ten Person"),
            new FlagMapExportSource("A-01", "A", 1, "Mary Ann Smith"),
            new FlagMapExportSource("A-02", "A", 2, "Robert Jones Jr.")
        ]);

        var bytes = FlagMapExcelExporter.Build(rows);

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var tableEntry = zip.GetEntry("xl/tables/table1.xml");
        Assert.NotNull(tableEntry);

        using var tableStream = tableEntry.Open();
        var tableXml = XDocument.Load(tableStream);
        var table = tableXml.Root;
        Assert.NotNull(table);
        Assert.Equal("table", table.Name.LocalName);
        Assert.Equal("FlagMap", table.Attribute("displayName")?.Value ?? table.Attribute("name")?.Value);
        Assert.Equal("A1:C4", table.Attribute("ref")?.Value);

        var autoFilter = table.Elements().Single(element => element.Name.LocalName == "autoFilter");
        Assert.Equal("A1:C4", autoFilter.Attribute("ref")?.Value);

        var headers = table
            .Elements()
            .Single(element => element.Name.LocalName == "tableColumns")
            .Elements()
            .Select(column => column.Attribute("name")!.Value)
            .ToArray();
        Assert.Equal(["First name", "Last name", "Flag grid"], headers);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        var excelTable = Assert.Single(worksheet.Tables);
        Assert.Equal(FlagMapExcelExporter.TableName, excelTable.Name);
        Assert.True(excelTable.ShowAutoFilter);
        Assert.Equal("Mary Ann", worksheet.Cell(2, 1).GetString());
        Assert.Equal("Smith", worksheet.Cell(2, 2).GetString());
        Assert.Equal("A-01", worksheet.Cell(2, 3).GetString());
        Assert.Equal("Robert", worksheet.Cell(3, 1).GetString());
        Assert.Equal("Jones Jr.", worksheet.Cell(3, 2).GetString());
        Assert.Equal("A-02", worksheet.Cell(3, 3).GetString());
        Assert.Equal("Ten", worksheet.Cell(4, 1).GetString());
        Assert.Equal("Person", worksheet.Cell(4, 2).GetString());
        Assert.Equal("A-10", worksheet.Cell(4, 3).GetString());
    }

    [Fact]
    public void Build_keeps_a_table_when_there_are_no_occupied_grids()
    {
        var bytes = FlagMapExcelExporter.Build([]);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        var excelTable = Assert.Single(worksheet.Tables);
        Assert.True(excelTable.ShowAutoFilter);
        Assert.Equal("First name", worksheet.Cell(1, 1).GetString());
        Assert.Equal("Last name", worksheet.Cell(1, 2).GetString());
        Assert.Equal("Flag grid", worksheet.Cell(1, 3).GetString());
        Assert.Equal(string.Empty, worksheet.Cell(2, 1).GetString());
        Assert.Equal(string.Empty, worksheet.Cell(2, 2).GetString());
    }
}
