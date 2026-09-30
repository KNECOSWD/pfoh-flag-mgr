using ClosedXML.Excel;

namespace PFOH.Api.Services;

public readonly record struct FlagMapExportSource(
    string FlagGridName,
    string RowLabel,
    int? ColumnNumber,
    string? HonoreeName);

public readonly record struct FlagMapExportRow(string FirstName, string LastName, string FlagGridName);

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
            .Select(position =>
            {
                var (firstName, lastName) = SplitHonoreeName(position.HonoreeName);
                return new FlagMapExportRow(firstName, lastName, position.FlagGridName.Trim());
            })
            .ToList();
    }

    /// <summary>
    /// Splits a display name built as given names, surname, optional generational suffix,
    /// and an optional trailing "(Nickname)".
    /// First name is every token before the surname. Last name is the surname plus a
    /// trailing Jr/Sr/II/III/IV (and a trailing nickname, when present).
    /// A single token is the last name. Blank input yields two empty strings.
    /// </summary>
    public static (string FirstName, string LastName) SplitHonoreeName(string? honoreeName)
    {
        if (string.IsNullOrWhiteSpace(honoreeName))
        {
            return (string.Empty, string.Empty);
        }

        var trimmed = honoreeName.Trim();
        var nickname = TakeTrailingParenthetical(ref trimmed);
        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return (string.Empty, nickname ?? string.Empty);
        }

        var surnameIndex = tokens.Length - 1;
        while (surnameIndex > 0 && IsGenerationalSuffix(tokens[surnameIndex]))
        {
            surnameIndex--;
        }

        var firstName = string.Join(' ', tokens.Take(surnameIndex));
        var lastName = string.Join(' ', tokens.Skip(surnameIndex));
        if (!string.IsNullOrEmpty(nickname))
        {
            lastName = string.IsNullOrEmpty(lastName) ? nickname : $"{lastName} {nickname}";
        }

        return (firstName, lastName);
    }

    public static byte[] Build(IReadOnlyList<FlagMapExportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(SheetName);

        worksheet.Cell(1, 1).Value = "First name";
        worksheet.Cell(1, 2).Value = "Last name";
        worksheet.Cell(1, 3).Value = "Flag grid";
        worksheet.Column(3).Style.NumberFormat.Format = "@";

        for (var index = 0; index < rows.Count; index++)
        {
            var excelRow = index + 2;
            worksheet.Cell(excelRow, 1).Value = rows[index].FirstName;
            worksheet.Cell(excelRow, 2).Value = rows[index].LastName;
            worksheet.Cell(excelRow, 3).SetValue(rows[index].FlagGridName);
        }

        var lastRow = Math.Max(1, rows.Count + 1);
        var table = worksheet.Range(1, 1, lastRow, 3).CreateTable(TableName);
        table.ShowAutoFilter = true;

        worksheet.SheetView.FreezeRows(1);
        worksheet.Column(1).AdjustToContents(1, lastRow);
        worksheet.Column(2).AdjustToContents(1, lastRow);
        worksheet.Column(3).AdjustToContents(1, lastRow);
        worksheet.Column(1).Width = Math.Max(worksheet.Column(1).Width, 18);
        worksheet.Column(2).Width = Math.Max(worksheet.Column(2).Width, 18);
        worksheet.Column(3).Width = Math.Max(worksheet.Column(3).Width, 12);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static bool IsGenerationalSuffix(string token)
    {
        var normalized = token.Trim().TrimEnd('.', ',');
        return normalized.Equals("Jr", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Sr", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("II", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("III", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("IV", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Display names append a nickname as a final "(Nickname)" token, including nicknames
    /// that contain spaces. Peel that group so it is not treated as the surname.
    /// </summary>
    private static string? TakeTrailingParenthetical(ref string value)
    {
        if (value.Length < 3 || value[^1] != ')')
        {
            return null;
        }

        var open = value.LastIndexOf('(');
        if (open <= 0 || !char.IsWhiteSpace(value[open - 1]))
        {
            return null;
        }

        var inside = value[(open + 1)..^1];
        if (inside.IndexOfAny(['(', ')']) >= 0)
        {
            return null;
        }

        var nickname = value[open..].Trim();
        value = value[..open].Trim();
        return string.IsNullOrEmpty(nickname) ? null : nickname;
    }
}
