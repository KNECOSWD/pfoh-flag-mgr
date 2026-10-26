using ClosedXML.Excel;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public static class RemovalQueueExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string SheetName = "Removal Queue";

    public const string TableName = "RemovalQueue";

    private static readonly string[] Headers =
    [
        "Flag spot",
        "Honoree",
        "Source",
        "What happened",
        "New flag spot",
        "Logged",
        "Logged by",
        "Status",
        "Finished",
        "Finished by"
    ];

    public static byte[] Build(IReadOnlyList<FlagRemovalQueueItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(SheetName);

        for (var column = 0; column < Headers.Length; column++)
        {
            worksheet.Cell(1, column + 1).Value = Headers[column];
        }

        worksheet.Column(1).Style.NumberFormat.Format = "@";
        worksheet.Column(5).Style.NumberFormat.Format = "@";

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = index + 2;
            var finished = item.CanceledUtc ?? item.DoneUtc;
            var values = new string?[]
            {
                item.FlagGridName,
                item.HonoreeName,
                item.Source,
                string.IsNullOrWhiteSpace(item.SourceDetail) ? null : item.SourceDetail.Trim(),
                item.NewFlagGridName,
                FormatCentral(item.CreatedUtc),
                item.CreatedBy,
                StatusOf(item),
                finished is DateTime done ? FormatCentral(done) : null,
                item.CanceledBy ?? item.DoneBy
            };

            for (var column = 0; column < values.Length; column++)
            {
                var cell = worksheet.Cell(row, column + 1);
                var value = values[column] ?? string.Empty;
                if (column is 0 or 4)
                {
                    cell.SetValue(value);
                }
                else
                {
                    cell.Value = value;
                }
            }
        }

        var lastRow = Math.Max(1, items.Count + 1);
        var table = worksheet.Range(1, 1, lastRow, Headers.Length).CreateTable(TableName);
        table.ShowAutoFilter = true;
        worksheet.SheetView.FreezeRows(1);
        for (var column = 1; column <= Headers.Length; column++)
        {
            worksheet.Column(column).AdjustToContents(1, lastRow);
            worksheet.Column(column).Width = Math.Max(worksheet.Column(column).Width, 14);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static string StatusOf(FlagRemovalQueueItem item)
    {
        if (item.CanceledUtc != null)
        {
            return "Canceled";
        }

        return item.DoneUtc != null ? "Done" : "Open";
    }

    private static string FormatCentral(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyy-MM-dd h:mm tt");
    }
}
