using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class HonoreeCardPdfLayoutTests
{
    private const string Name = "Salvador Guzman";
    private const string Years = "1942 - 1945";
    private const string Branch = "United States Army";
    private const double BioLeft = 40;

    [Fact]
    public void Short_bio_stays_together_on_the_left_and_the_header_stays_centered()
    {
        const string tribute = "Our Hero!";
        var runs = Render(tribute);

        var bio = BioRuns(runs, tribute);
        var line = Assert.Single(bio);
        Assert.Equal(tribute, line.Text);
        Assert.Equal(BioLeft, line.X, 1);
        Assert.DoesNotContain("  ", line.Text);
        AssertHeaderStaysCentered(runs, line.X);
    }

    [Fact]
    public void Long_bio_lines_are_left_aligned_with_no_full_justification()
    {
        const string tribute =
            "Our Hero! He served with quiet courage and the family kept every letter he sent home from the field. " +
            "They read those letters on the porch and saved each one in a box by the window.";
        var runs = Render(tribute);
        var bio = BioRuns(runs, tribute);

        Assert.True(bio.Count >= 3, Dump(runs));
        Assert.All(bio, run => Assert.Equal(BioLeft, run.X, 1));
        Assert.All(bio.GroupBy(run => Math.Round(run.Y, 1)), line => Assert.Single(line));
        Assert.All(bio, run => Assert.DoesNotContain("  ", run.Text));
        Assert.Equal(tribute, string.Join(" ", bio.OrderByDescending(run => run.Y).Select(run => run.Text)));
        AssertHeaderStaysCentered(runs, bio[0].X);
    }

    [Fact]
    public void Short_bio_shows_conflicts_and_awards_left_aligned_under_the_bio()
    {
        const string tribute = "Our Hero!";
        var runs = Render(tribute, conflicts: "World War II", awards: "Silver Star");
        var bio = Assert.Single(BioRuns(runs, tribute));

        var conflictsLabel = Assert.Single(runs, run => run.Text == "Conflicts served");
        var conflictsValue = Assert.Single(runs, run => run.Text == "World War II");
        var awardsLabel = Assert.Single(runs, run => run.Text == "Awards");
        var awardsValue = Assert.Single(runs, run => run.Text == "Silver Star");

        Assert.All(
            new[] { conflictsLabel, conflictsValue, awardsLabel, awardsValue },
            run => Assert.Equal(BioLeft, run.X, 1));
        Assert.True(conflictsLabel.Y < bio.Y, Dump(runs));
        Assert.True(conflictsValue.Y < conflictsLabel.Y, Dump(runs));
        Assert.True(awardsLabel.Y < conflictsValue.Y, Dump(runs));
        Assert.True(awardsValue.Y < awardsLabel.Y, Dump(runs));
        AssertHeaderStaysCentered(runs, bio.X);
    }

    [Fact]
    public void Long_bio_omits_conflicts_and_awards_instead_of_crowding_the_card()
    {
        var tribute = string.Join(" ", Enumerable.Repeat(
            "The family kept every letter from the field and read them on the porch.",
            40));
        var runs = Render(tribute, conflicts: "Operation Overlord", awards: "Medal of Honor");

        Assert.True(BioRuns(runs, tribute).Count >= 3, Dump(runs));
        Assert.DoesNotContain(runs, run => run.Text.Contains("Conflicts served", StringComparison.Ordinal));
        Assert.DoesNotContain(runs, run => run.Text.Contains("Operation Overlord", StringComparison.Ordinal));
        Assert.DoesNotContain(runs, run => run.Text.Contains("Awards", StringComparison.Ordinal));
        Assert.DoesNotContain(runs, run => run.Text.Contains("Medal of Honor", StringComparison.Ordinal));
    }

    [Fact]
    public void A_section_that_cannot_fit_whole_is_omitted_and_conflicts_are_preferred()
    {
        var huge = string.Join(", ", Enumerable.Repeat("Distinguished Service Cross", 80));
        var conflictsOnly = Render("Our Hero!", conflicts: "Korea", awards: huge);
        Assert.Contains(conflictsOnly, run => run.Text == "Conflicts served");
        Assert.Contains(conflictsOnly, run => run.Text == "Korea");
        Assert.DoesNotContain(conflictsOnly, run => run.Text.Contains("Awards", StringComparison.Ordinal));
        Assert.DoesNotContain(conflictsOnly, run => run.Text.Contains("Distinguished Service Cross", StringComparison.Ordinal));

        var awardsOnly = Render("Our Hero!", conflicts: huge, awards: "Purple Heart");
        Assert.Contains(awardsOnly, run => run.Text == "Awards");
        Assert.Contains(awardsOnly, run => run.Text == "Purple Heart");
        Assert.DoesNotContain(awardsOnly, run => run.Text.Contains("Conflicts served", StringComparison.Ordinal));
        Assert.DoesNotContain(awardsOnly, run => run.Text.Contains("Distinguished Service Cross", StringComparison.Ordinal));
    }

    private static void AssertHeaderStaysCentered(IReadOnlyList<TextRun> runs, double bioLeft)
    {
        foreach (var header in new[] { Name, Years, Branch })
        {
            var run = Assert.Single(runs, item => item.Text == header);
            Assert.True(run.X > bioLeft + 20, $"{header} moved to the bio's left edge at x={run.X:0.0}. {Dump(runs)}");
        }
    }

    private static List<TextRun> BioRuns(IReadOnlyList<TextRun> runs, string tribute)
    {
        var bio = runs.Where(run => run.Text.Length > 0 && tribute.Contains(run.Text, StringComparison.Ordinal)).ToList();
        Assert.True(bio.Count > 0, Dump(runs));
        return bio;
    }

    private static List<TextRun> Render(string tribute, string? conflicts = null, string? awards = null)
    {
        var pdf = HonoreeReportPdfGenerator.Create(
            new EmptyHost(),
            new Honoree
            {
                FirstName = "Salvador",
                LastName = "Guzman",
                StartYear = 1942,
                EndYear = 1945,
                Description = tribute,
                ConflictsServed = conflicts ?? string.Empty,
                Awards = awards ?? string.Empty,
                Rank = "Private",
                ServiceBranch = new ServiceBranch { ServiceBranchName = Branch },
                FlagGrid = new FlagGrid { FlagGridName = "V-12" }
            },
            searchResult: null,
            photoBytes: null);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        return ReadRuns(pdf);
    }

    private static string Dump(IEnumerable<TextRun> runs) =>
        string.Join(" | ", runs.Select(run => $"{run.X.ToString("0.0", CultureInfo.InvariantCulture)},{run.Y.ToString("0.0", CultureInfo.InvariantCulture)}:{run.Text}"));

    private sealed record TextRun(double X, double Y, string Text);

    private sealed class EmptyHost : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PFOH.Api.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static List<TextRun> ReadRuns(byte[] pdf)
    {
        var objects = ReadObjects(pdf);
        var page = objects.Values.Select(Encoding.Latin1.GetString).First(text => text.Contains("/Type /Page\n") || text.Contains("/Type /Page\r"));
        var contentsId = int.Parse(Regex.Match(page, @"/Contents\s+(\d+)\s+0\s+R").Groups[1].Value, CultureInfo.InvariantCulture);
        var fonts = new Dictionary<string, Dictionary<int, char>>(StringComparer.Ordinal);
        var fontBlock = Regex.Match(page, @"/Font\s*<<([^>]*)>>").Groups[1].Value;
        foreach (Match match in Regex.Matches(fontBlock, @"/(F\d+)\s+(\d+)\s+0\s+R"))
        {
            var fontObject = Encoding.Latin1.GetString(objects[int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)]);
            var mapId = int.Parse(Regex.Match(fontObject, @"/ToUnicode\s+(\d+)\s+0\s+R").Groups[1].Value, CultureInfo.InvariantCulture);
            fonts[match.Groups[1].Value] = ParseCMap(Inflate(objects[mapId]));
        }

        return ParseContent(Encoding.Latin1.GetString(Inflate(objects[contentsId])), fonts);
    }

    private static List<TextRun> ParseContent(string content, IReadOnlyDictionary<string, Dictionary<int, char>> fonts)
    {
        var runs = new List<TextRun>();
        double x = 0;
        double y = 0;
        var font = "";
        var operands = new List<string>();
        var index = 0;

        while (index < content.Length)
        {
            var ch = content[index];
            if (char.IsWhiteSpace(ch))
            {
                index++;
                continue;
            }

            if (ch == '%')
            {
                while (index < content.Length && content[index] != '\n') index++;
                continue;
            }

            if (ch == '<')
            {
                var end = content.IndexOf('>', index);
                operands.Add(content[index..(end + 1)]);
                index = end + 1;
                continue;
            }

            if (ch == '(')
            {
                operands.Add(ReadLiteral(content, ref index));
                continue;
            }

            if (ch == '/')
            {
                var start = index;
                index++;
                while (index < content.Length && !char.IsWhiteSpace(content[index]) && "/[]<>()".IndexOf(content[index]) < 0)
                {
                    index++;
                }

                operands.Add(content[start..index]);
                continue;
            }

            if (char.IsDigit(ch) || ch is '-' or '+' or '.')
            {
                var start = index;
                index++;
                while (index < content.Length && (char.IsDigit(content[index]) || content[index] is '.' or '-' or '+'))
                {
                    index++;
                }

                operands.Add(content[start..index]);
                continue;
            }

            var wordStart = index;
            while (index < content.Length && (char.IsLetter(content[index]) || content[index] is '\'' or '"'))
            {
                index++;
                if (content[index - 1] is '\'' or '"') break;
            }

            if (index == wordStart)
            {
                index++;
                continue;
            }

            var op = content[wordStart..index];
            switch (op)
            {
                case "BT":
                    x = 0;
                    y = 0;
                    break;
                case "Tf":
                    var fontName = operands.LastOrDefault(operand => operand.StartsWith('/'));
                    if (fontName is not null)
                    {
                        font = fontName[1..];
                    }

                    break;
                case "Tm" when operands.Count >= 6:
                    x = Parse(operands[^2]);
                    y = Parse(operands[^1]);
                    break;
                case "Td":
                case "TD":
                    x += Parse(operands[^2]);
                    y += Parse(operands[^1]);
                    break;
                case "Tj" when operands.Count >= 1:
                    runs.Add(new TextRun(x, y, Decode(operands[^1], font, fonts)));
                    break;
                case "TJ":
                    var combined = string.Concat(operands
                        .Where(operand => operand.StartsWith('<') || operand.StartsWith('('))
                        .Select(operand => Decode(operand, font, fonts)));
                    if (combined.Length > 0)
                    {
                        runs.Add(new TextRun(x, y, combined));
                    }

                    break;
            }

            operands.Clear();
        }

        return runs;
    }

    private static string Decode(string token, string font, IReadOnlyDictionary<string, Dictionary<int, char>> fonts)
    {
        if (token.StartsWith('('))
        {
            return token[1..^1];
        }

        var map = fonts[font];
        var hex = string.Concat(token.Where(Uri.IsHexDigit));
        var text = new StringBuilder(hex.Length / 4);
        for (var i = 0; i + 3 < hex.Length; i += 4)
        {
            var code = int.Parse(hex.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            text.Append(map.TryGetValue(code, out var glyph) ? glyph : '?');
        }

        return text.ToString();
    }

    private static Dictionary<int, char> ParseCMap(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var map = new Dictionary<int, char>();
        foreach (Match match in Regex.Matches(text, @"<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4})>"))
        {
            var start = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var end = int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var dest = int.Parse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            for (var code = start; code <= end; code++)
            {
                map[code] = (char)(dest + (code - start));
            }
        }

        return map;
    }

    private static Dictionary<int, byte[]> ReadObjects(byte[] pdf)
    {
        var start = LastIndexOf(pdf, "startxref"u8);
        var tail = Encoding.ASCII.GetString(pdf, start, Math.Min(64, pdf.Length - start));
        var xrefAt = int.Parse(tail.Split('\n', '\r')[1], CultureInfo.InvariantCulture);
        var xref = Encoding.ASCII.GetString(pdf, xrefAt, Math.Min(pdf.Length - xrefAt, 8000));
        var objects = new Dictionary<int, byte[]>();
        var lines = xref.Split('\n');
        var index = 1;
        while (index < lines.Length && !lines[index].StartsWith("trailer", StringComparison.Ordinal))
        {
            var header = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length != 2)
            {
                break;
            }

            var first = int.Parse(header[0], CultureInfo.InvariantCulture);
            var count = int.Parse(header[1], CultureInfo.InvariantCulture);
            for (var i = 0; i < count; i++)
            {
                var parts = lines[index + 1 + i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[2] == "n")
                {
                    var offset = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    objects[first + i] = SliceObject(pdf, offset);
                }
            }

            index += count + 1;
        }

        return objects;
    }

    private static byte[] SliceObject(byte[] pdf, int offset)
    {
        var streamAt = IndexOf(pdf, "stream"u8, offset);
        var endObj = IndexOf(pdf, "endobj"u8, offset);
        if (streamAt < 0 || endObj < streamAt)
        {
            return pdf[offset..(endObj + "endobj".Length)];
        }

        var dict = Encoding.ASCII.GetString(pdf, offset, streamAt - offset);
        var length = int.Parse(Regex.Match(dict, @"/Length\s+(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var dataStart = streamAt + "stream".Length;
        if (pdf[dataStart] == '\r') dataStart++;
        if (pdf[dataStart] == '\n') dataStart++;
        var after = dataStart + length;
        endObj = IndexOf(pdf, "endobj"u8, after);
        return pdf[offset..(endObj + "endobj".Length)];
    }

    private static byte[] Inflate(byte[] obj)
    {
        var streamAt = IndexOf(obj, "stream"u8, 0);
        if (streamAt < 0)
        {
            return obj;
        }

        var dict = Encoding.ASCII.GetString(obj, 0, streamAt);
        var length = int.Parse(Regex.Match(dict, @"/Length\s+(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var dataStart = streamAt + "stream".Length;
        if (obj[dataStart] == '\r') dataStart++;
        if (obj[dataStart] == '\n') dataStart++;
        var raw = obj[dataStart..(dataStart + length)];
        if (!dict.Contains("/FlateDecode", StringComparison.Ordinal))
        {
            return raw;
        }

        using var input = new MemoryStream(raw);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static string ReadLiteral(string content, ref int index)
    {
        var start = index;
        index++;
        while (index < content.Length)
        {
            if (content[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (content[index] == ')')
            {
                index++;
                break;
            }

            index++;
        }

        return content[start..index];
    }

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int start)
    {
        var found = haystack.AsSpan(start).IndexOf(needle);
        return found < 0 ? -1 : start + found;
    }

    private static int LastIndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var start = haystack.Length - needle.Length; start >= 0; start--)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return start;
            }
        }

        return -1;
    }
}
