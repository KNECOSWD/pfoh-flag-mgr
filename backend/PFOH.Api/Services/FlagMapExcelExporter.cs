using ClosedXML.Excel;

namespace PFOH.Api.Services;

public readonly record struct FlagMapExportSource(
    string FlagGridName,
    string RowLabel,
    int? ColumnNumber,
    string? HonoreeName,
    bool IsOpen = false,
    bool IsReserved = false,
    string? Rank = null,
    string? ServiceBranchName = null,
    string? SponsorName = null,
    bool Kia = false);

public readonly record struct FlagMapExportRow(
    string FirstName,
    string LastName,
    string FullName,
    string FlagGridName,
    string Status,
    string Rank,
    string ServiceBranch,
    string SponsorName,
    string Kia);

public static class FlagMapExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string SheetName = "Flag Map";

    public const string TableName = "FlagMap";

    public const string FirstNameColumn = "firstName";
    public const string LastNameColumn = "lastName";
    public const string FullNameColumn = "fullName";
    public const string FlagGridColumn = "flagGrid";
    public const string StatusColumn = "status";
    public const string RankColumn = "rank";
    public const string ServiceBranchColumn = "serviceBranch";
    public const string SponsorNameColumn = "sponsorName";
    public const string KiaColumn = "kia";

    /// <summary>
    /// Locked offer set, in workbook order. Callers cannot add columns outside this list.
    /// </summary>
    public static readonly IReadOnlyList<string> ColumnKeys =
    [
        FirstNameColumn,
        LastNameColumn,
        FullNameColumn,
        FlagGridColumn,
        StatusColumn,
        RankColumn,
        ServiceBranchColumn,
        SponsorNameColumn,
        KiaColumn
    ];

    public static readonly IReadOnlyDictionary<string, string> ColumnHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FirstNameColumn] = "First Name",
            [LastNameColumn] = "Last Name",
            [FullNameColumn] = "Full Name",
            [FlagGridColumn] = "Flag Grid",
            [StatusColumn] = "Status",
            [RankColumn] = "Rank",
            [ServiceBranchColumn] = "Service Branch",
            [SponsorNameColumn] = "Sponsor Name",
            [KiaColumn] = "KIA"
        };

    public static IReadOnlyList<string> DefaultColumns => ColumnKeys;

    public static bool TryParseColumns(string? columns, out IReadOnlyList<string> selected, out string? error)
    {
        if (columns is null)
        {
            selected = ColumnKeys;
            error = null;
            return true;
        }

        if (columns.Length > 200)
        {
            selected = [];
            error = "Select at least one export field.";
            return false;
        }

        var requested = columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0)
        {
            selected = [];
            error = "Select at least one export field.";
            return false;
        }

        var known = new HashSet<string>(ColumnKeys, StringComparer.OrdinalIgnoreCase);
        if (requested.Any(key => !known.Contains(key)))
        {
            selected = [];
            error = "One or more export fields are not available.";
            return false;
        }

        var requestedSet = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        selected = ColumnKeys.Where(key => requestedSet.Contains(key)).ToArray();
        error = null;
        return true;
    }

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
                var fullName = position.HonoreeName!.Trim();
                var (firstName, lastName) = SplitHonoreeName(fullName);
                return new FlagMapExportRow(
                    firstName,
                    lastName,
                    fullName,
                    position.FlagGridName.Trim(),
                    BuildStatus(position),
                    Clean(position.Rank),
                    Clean(position.ServiceBranchName),
                    Clean(position.SponsorName),
                    position.Kia ? "Yes" : "No");
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

    public static byte[] Build(IReadOnlyList<FlagMapExportRow> rows, IReadOnlyList<string>? columns = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var selected = NormalizeColumns(columns);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(SheetName);

        for (var columnIndex = 0; columnIndex < selected.Count; columnIndex++)
        {
            var columnKey = selected[columnIndex];
            var excelColumn = columnIndex + 1;
            worksheet.Cell(1, excelColumn).Value = ColumnHeaders[columnKey];
            if (columnKey == FlagGridColumn)
            {
                worksheet.Column(excelColumn).Style.NumberFormat.Format = "@";
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var excelRow = index + 2;
            for (var columnIndex = 0; columnIndex < selected.Count; columnIndex++)
            {
                var columnKey = selected[columnIndex];
                var cell = worksheet.Cell(excelRow, columnIndex + 1);
                var value = CellValue(rows[index], columnKey);
                if (columnKey == FlagGridColumn)
                {
                    cell.SetValue(value);
                }
                else
                {
                    cell.Value = value;
                }
            }
        }

        var lastRow = Math.Max(1, rows.Count + 1);
        var table = worksheet.Range(1, 1, lastRow, selected.Count).CreateTable(TableName);
        table.ShowAutoFilter = true;

        worksheet.SheetView.FreezeRows(1);
        for (var columnIndex = 1; columnIndex <= selected.Count; columnIndex++)
        {
            worksheet.Column(columnIndex).AdjustToContents(1, lastRow);
            var minimum = selected[columnIndex - 1] == FlagGridColumn ? 12 : 14;
            worksheet.Column(columnIndex).Width = Math.Max(worksheet.Column(columnIndex).Width, minimum);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static string BuildStatus(FlagMapExportSource position)
    {
        if (position.IsReserved)
        {
            return "Reserved";
        }

        if (position.IsOpen)
        {
            return "Open";
        }

        return "Occupied";
    }

    private static IReadOnlyList<string> NormalizeColumns(IReadOnlyList<string>? columns)
    {
        if (columns is null)
        {
            return ColumnKeys;
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException("Select at least one export field.", nameof(columns));
        }

        var known = new HashSet<string>(ColumnKeys, StringComparer.Ordinal);
        if (columns.Any(key => !known.Contains(key)))
        {
            throw new ArgumentException("One or more export fields are not available.", nameof(columns));
        }

        var requested = new HashSet<string>(columns, StringComparer.Ordinal);
        var selected = ColumnKeys.Where(requested.Contains).ToArray();
        if (selected.Length == 0)
        {
            throw new ArgumentException("Select at least one export field.", nameof(columns));
        }

        return selected;
    }

    private static string CellValue(FlagMapExportRow row, string column) => column switch
    {
        FirstNameColumn => row.FirstName,
        LastNameColumn => row.LastName,
        FullNameColumn => row.FullName,
        FlagGridColumn => row.FlagGridName,
        StatusColumn => row.Status,
        RankColumn => row.Rank,
        ServiceBranchColumn => row.ServiceBranch,
        SponsorNameColumn => row.SponsorName,
        KiaColumn => row.Kia,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown export column.")
    };

    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

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
