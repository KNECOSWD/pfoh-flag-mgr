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
            new FlagMapExportSource("A-01", "A", 1, " One Person ")
        ]);

        Assert.Equal(
            ["A-01", "A-2", "A-10", "B-01"],
            rows.Select(row => row.FlagGridName).ToArray());
        Assert.Equal("One Person", rows[0].HonoreeName);
        Assert.Equal("Two Person", rows[1].HonoreeName);
    }

    [Fact]
    public void Build_writes_an_excel_table_with_autofilter()
    {
        var rows = FlagMapExcelExporter.SelectRows(
        [
            new FlagMapExportSource("A-10", "A", 10, "Ten Person"),
            new FlagMapExportSource("A-01", "A", 1, "One Person")
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
        Assert.Equal("A1:B3", table.Attribute("ref")?.Value);

        var autoFilter = table.Elements().Single(element => element.Name.LocalName == "autoFilter");
        Assert.Equal("A1:B3", autoFilter.Attribute("ref")?.Value);

        var headers = table
            .Elements()
            .Single(element => element.Name.LocalName == "tableColumns")
            .Elements()
            .Select(column => column.Attribute("name")!.Value)
            .ToArray();
        Assert.Equal(["Honoree name", "Flag grid"], headers);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        var excelTable = Assert.Single(worksheet.Tables);
        Assert.Equal(FlagMapExcelExporter.TableName, excelTable.Name);
        Assert.True(excelTable.ShowAutoFilter);
        Assert.Equal("One Person", worksheet.Cell(2, 1).GetString());
        Assert.Equal("A-01", worksheet.Cell(2, 2).GetString());
        Assert.Equal("Ten Person", worksheet.Cell(3, 1).GetString());
        Assert.Equal("A-10", worksheet.Cell(3, 2).GetString());
    }

    [Fact]
    public void Build_keeps_a_table_when_there_are_no_occupied_grids()
    {
        var bytes = FlagMapExcelExporter.Build([]);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        var excelTable = Assert.Single(worksheet.Tables);
        Assert.True(excelTable.ShowAutoFilter);
        Assert.Equal("Honoree name", worksheet.Cell(1, 1).GetString());
        Assert.Equal("Flag grid", worksheet.Cell(1, 2).GetString());
        Assert.Equal(string.Empty, worksheet.Cell(2, 1).GetString());
    }
}
