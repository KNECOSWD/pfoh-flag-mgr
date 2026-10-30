using System.Text;
using PFOH.Api.Dtos;

namespace PFOH.Api.Services;

public static class ReactivationCsv
{
    public const string Header = "honoree id,name,requester email,link,expiry,eligibility reason";

    public static string Write(IReadOnlyList<ReactivationLinkRowDto> rows)
    {
        var builder = new StringBuilder();
        builder.Append(Header).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(Cell(row.HonoreeId)).Append(',')
                .Append(Cell(row.Name)).Append(',')
                .Append(Cell(row.RequesterEmail)).Append(',')
                .Append(Cell(row.Link)).Append(',')
                .Append(Cell(row.Expiry)).Append(',')
                .Append(Cell(row.EligibilityReason))
                .Append("\r\n");
        }

        return builder.ToString();
    }

    public static IReadOnlyList<(string HonoreeId, string? Email)> Parse(string? text)
    {
        var rows = new List<(string HonoreeId, string? Email)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return rows;
        }

        var records = SplitRecords(text);
        if (records.Count == 0)
        {
            return rows;
        }

        var header = false;
        var idIndex = 0;
        var emailIndex = 1;
        var first = records[0];
        if (first.Count > 0 && IsIdHeader(first[0]))
        {
            header = true;
            idIndex = IndexOf(first, IsIdHeader);
            emailIndex = IndexOf(first, IsEmailHeader);
        }

        for (var i = header ? 1 : 0; i < records.Count; i++)
        {
            var cells = records[i];
            if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var id = idIndex >= 0 && idIndex < cells.Count ? cells[idIndex].Trim() : string.Empty;
            string? email = null;
            if (emailIndex >= 0 && emailIndex < cells.Count && !string.IsNullOrWhiteSpace(cells[emailIndex]))
            {
                email = cells[emailIndex].Trim();
            }

            if (id.Length == 0 && email is null)
            {
                continue;
            }

            rows.Add((id, email));
        }

        return rows;
    }

    private static int IndexOf(IReadOnlyList<string> cells, Func<string, bool> match)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (match(cells[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsIdHeader(string value)
    {
        var key = NormalizeHeader(value);
        return key is "honoreeid" or "id" or "honoree";
    }

    private static bool IsEmailHeader(string value)
    {
        var key = NormalizeHeader(value);
        return key is "email" or "requesteremail" or "submitteremail";
    }

    private static string NormalizeHeader(string value) =>
        value.Trim().TrimStart('\uFEFF').Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private static List<List<string>> SplitRecords(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                records.Add(row);
                row = new List<string>();
            }
            else if (ch != '\r')
            {
                cell.Append(ch);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            records.Add(row);
        }

        return records;
    }

    internal static string Neutralize(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            return "'" + text;
        }

        return text;
    }

    private static string Cell(string? value)
    {
        var text = Neutralize(value);
        if (text.Contains('"', StringComparison.Ordinal) ||
            text.Contains(',', StringComparison.Ordinal) ||
            text.Contains('\n', StringComparison.Ordinal) ||
            text.Contains('\r', StringComparison.Ordinal) ||
            text.Contains('\t', StringComparison.Ordinal))
        {
            return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return text;
    }
}
