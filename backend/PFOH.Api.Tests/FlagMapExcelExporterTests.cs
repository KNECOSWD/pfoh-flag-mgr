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
        Assert.Equal("A1:I4", table.Attribute("ref")?.Value);

        var autoFilter = table.Elements().Single(element => element.Name.LocalName == "autoFilter");
        Assert.Equal("A1:I4", autoFilter.Attribute("ref")?.Value);

        var headers = table
            .Elements()
            .Single(element => element.Name.LocalName == "tableColumns")
            .Elements()
            .Select(column => column.Attribute("name")!.Value)
            .ToArray();
        Assert.Equal(LockedHeaders, headers);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        var excelTable = Assert.Single(worksheet.Tables);
        Assert.Equal(FlagMapExcelExporter.TableName, excelTable.Name);
        Assert.True(excelTable.ShowAutoFilter);
        Assert.Equal("Mary Ann", worksheet.Cell(2, 1).GetString());
        Assert.Equal("Smith", worksheet.Cell(2, 2).GetString());
        Assert.Equal("Mary Ann Smith", worksheet.Cell(2, 3).GetString());
        Assert.Equal("A-01", worksheet.Cell(2, 4).GetString());
        Assert.Equal("Occupied", worksheet.Cell(2, 5).GetString());
        Assert.Equal("Robert", worksheet.Cell(3, 1).GetString());
        Assert.Equal("Jones Jr.", worksheet.Cell(3, 2).GetString());
        Assert.Equal("A-02", worksheet.Cell(3, 4).GetString());
        Assert.Equal("Ten", worksheet.Cell(4, 1).GetString());
        Assert.Equal("Person", worksheet.Cell(4, 2).GetString());
        Assert.Equal("A-10", worksheet.Cell(4, 4).GetString());
    }

    [Fact]
    public void Build_default_columns_are_the_locked_offer_set_and_nothing_else()
    {
        Assert.Equal(
            [
                "firstName",
                "lastName",
                "fullName",
                "flagGrid",
                "status",
                "rank",
                "serviceBranch",
                "sponsorName",
                "kia"
            ],
            FlagMapExcelExporter.ColumnKeys);

        var parsed = FlagMapExcelExporter.TryParseColumns(null, out var selected, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(FlagMapExcelExporter.ColumnKeys, selected);

        var bytes = FlagMapExcelExporter.Build(
        [
            new FlagMapExportRow(
                "Mary Ann",
                "Jones III",
                "Mary Ann Jones III",
                "A-01",
                "Occupied",
                "SGT",
                "Army",
                "Jane Sponsor",
                "Yes")
        ]);

        var headers = ReadHeaders(bytes);
        Assert.Equal(LockedHeaders, headers);
        Assert.DoesNotContain(headers, header => InventedHeaders.Contains(header));
    }

    [Fact]
    public void Build_omits_an_unchecked_field_and_keeps_flag_grid_when_checked()
    {
        var rows = FlagMapExcelExporter.SelectRows(
        [
            new FlagMapExportSource(
                "A-01",
                "A",
                1,
                "Mary Ann Jones III",
                Rank: "SGT",
                ServiceBranchName: "Army",
                SponsorName: "Jane Sponsor",
                Kia: true),
            new FlagMapExportSource("A-02", "A", 2, null, IsOpen: true),
            new FlagMapExportSource("C-01", "C", 1, "   ", IsReserved: true)
        ]);

        var only = Assert.Single(rows);
        Assert.Equal("A-01", only.FlagGridName);
        Assert.Equal("Mary Ann", only.FirstName);
        Assert.Equal("Jones III", only.LastName);
        Assert.Equal("Mary Ann Jones III", only.FullName);
        Assert.Equal("Occupied", only.Status);
        Assert.Equal("SGT", only.Rank);
        Assert.Equal("Army", only.ServiceBranch);
        Assert.Equal("Jane Sponsor", only.SponsorName);
        Assert.Equal("Yes", only.Kia);

        var parsed = FlagMapExcelExporter.TryParseColumns(
            "kia,flagGrid,sponsorName,kia",
            out var selected,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(["flagGrid", "sponsorName", "kia"], selected);

        var bytes = FlagMapExcelExporter.Build(rows, selected);
        var headers = ReadHeaders(bytes);
        Assert.Equal(["Flag Grid", "Sponsor Name", "KIA"], headers);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        Assert.Equal("A-01", worksheet.Cell(2, 1).GetString());
        Assert.Equal("Jane Sponsor", worksheet.Cell(2, 2).GetString());
        Assert.Equal("Yes", worksheet.Cell(2, 3).GetString());
        Assert.Equal(string.Empty, worksheet.Cell(1, 4).GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("nickname")]
    [InlineData("middleName")]
    [InlineData("description,serviceYears,submitter,claimants")]
    [InlineData("firstName,nickname")]
    public void TryParseColumns_rejects_an_empty_selection_or_fields_outside_the_offer_set(string columns)
    {
        var parsed = FlagMapExcelExporter.TryParseColumns(columns, out var selected, out var error);

        Assert.False(parsed);
        Assert.Empty(selected);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void SelectRows_writes_status_rank_branch_sponsor_and_kia_from_the_source()
    {
        var rows = FlagMapExcelExporter.SelectRows(
        [
            new FlagMapExportSource(
                "B-01",
                "B",
                1,
                "Robert Jones Jr.",
                IsReserved: true,
                Rank: " CPT ",
                ServiceBranchName: " Navy ",
                SponsorName: "  ",
                Kia: false),
            new FlagMapExportSource(
                "A-01",
                "A",
                1,
                "Mary Ann Smith (Mae)",
                Rank: "SFC",
                ServiceBranchName: "Army",
                SponsorName: " Pat Q. Sponsor Sr. ",
                Kia: true)
        ]);

        Assert.Equal(["A-01", "B-01"], rows.Select(row => row.FlagGridName).ToArray());

        Assert.Equal("Mary Ann", rows[0].FirstName);
        Assert.Equal("Smith (Mae)", rows[0].LastName);
        Assert.Equal("Mary Ann Smith (Mae)", rows[0].FullName);
        Assert.Equal("Occupied", rows[0].Status);
        Assert.Equal("SFC", rows[0].Rank);
        Assert.Equal("Army", rows[0].ServiceBranch);
        Assert.Equal("Pat Q. Sponsor Sr.", rows[0].SponsorName);
        Assert.Equal("Yes", rows[0].Kia);

        Assert.Equal("Robert", rows[1].FirstName);
        Assert.Equal("Jones Jr.", rows[1].LastName);
        Assert.Equal("Reserved", rows[1].Status);
        Assert.Equal("CPT", rows[1].Rank);
        Assert.Equal("Navy", rows[1].ServiceBranch);
        Assert.Equal(string.Empty, rows[1].SponsorName);
        Assert.Equal("No", rows[1].Kia);
    }

    [Fact]
    public void Build_keeps_a_table_when_there_are_no_occupied_grids()
    {
        var bytes = FlagMapExcelExporter.Build([]);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        var excelTable = Assert.Single(worksheet.Tables);
        Assert.True(excelTable.ShowAutoFilter);
        Assert.Equal(LockedHeaders, ReadHeaders(bytes));
        Assert.Equal(string.Empty, worksheet.Cell(2, 1).GetString());
        Assert.Equal(string.Empty, worksheet.Cell(2, 2).GetString());
    }

    private static readonly string[] LockedHeaders =
    [
        "First Name",
        "Last Name",
        "Full Name",
        "Flag Grid",
        "Status",
        "Rank",
        "Service Branch",
        "Sponsor Name",
        "KIA"
    ];

    private static readonly string[] InventedHeaders =
    [
        "Middle Name",
        "Nickname",
        "Description",
        "Service Years",
        "Submitter",
        "Claimants",
        "Honoree name"
    ];

    private static string[] ReadHeaders(byte[] bytes)
    {
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        var headers = new List<string>();
        for (var column = 1; worksheet.Cell(1, column).GetString().Length > 0; column++)
        {
            headers.Add(worksheet.Cell(1, column).GetString());
        }

        return headers.ToArray();
    }
}
