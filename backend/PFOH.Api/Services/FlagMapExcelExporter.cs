using ClosedXML.Excel;

namespace PFOH.Api.Services;

public readonly record struct FlagMapExportSource(
    string FlagGridName,
    string RowLabel,
    int? ColumnNumber,
    string? HonoreeName);

public readonly record struct FlagMapExportRow(string HonoreeName, string FlagGridName);

public static class FlagMapExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string SheetName = "Flag Map";

    public const string TableName = "FlagMap";

    public static IReadOnlyList<FlagMapExportRow> SelectRows(IEnumerable<FlagMapExportSource> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);

        return positions
            .Where(position =>
                !string.IsNullOrWhiteSpace(position.HonoreeName) &&
                !string.IsNullOrWhiteSpace(position.FlagGridName))
            .OrderBy(position => position.RowLabel ?? string.Empty)
            .ThenBy(position => position.ColumnNumber ?? int.MaxValue)
            .ThenBy(position => position.FlagGridName)
            .ThenBy(position => position.HonoreeName)
            .Select(position => new FlagMapExportRow(
                position.HonoreeName!.Trim(),
                position.FlagGridName.Trim()))
            .ToList();
    }

    public static byte[] Build(IReadOnlyList<FlagMapExportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(SheetName);

        worksheet.Cell(1, 1).Value = "Honoree name";
        worksheet.Cell(1, 2).Value = "Flag grid";
        worksheet.Column(2).Style.NumberFormat.Format = "@";

        for (var index = 0; index < rows.Count; index++)
        {
            var excelRow = index + 2;
            worksheet.Cell(excelRow, 1).Value = rows[index].HonoreeName;
            worksheet.Cell(excelRow, 2).SetValue(rows[index].FlagGridName);
        }

        var lastRow = Math.Max(1, rows.Count + 1);
        var table = worksheet.Range(1, 1, lastRow, 2).CreateTable(TableName);
        table.ShowAutoFilter = true;

        worksheet.SheetView.FreezeRows(1);
        worksheet.Column(1).AdjustToContents(1, lastRow);
        worksheet.Column(2).AdjustToContents(1, lastRow);
        worksheet.Column(1).Width = Math.Max(worksheet.Column(1).Width, 18);
        worksheet.Column(2).Width = Math.Max(worksheet.Column(2).Width, 12);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
