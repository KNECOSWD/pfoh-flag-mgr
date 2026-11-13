using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Drawing.Layout;
using PdfSharpCore.Drawing.Layout.enums;
using PdfSharpCore.Pdf;
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
    public void Full_header_bio_at_the_limit_keeps_conflicts_and_awards_above_the_rotary_mark()
    {
        const string conflicts = "World War II, Korean War";
        const string awards = "Purple Heart, Bronze Star, Distinguished Flying Cross";
        const string honors =
            "SERGEANT JAMES ALLEY RECEIVED THE DISTINGUISHED FLYING CROSS, PURPLE HEART, AND BRONZE STAR. CAPTAIN SALVADOR GUZMAN SERVED WITH THE UNITED STATES ARMY AIR CORPS BESIDE LIEUTENANT MARGARET COLE. ";
        const string porch = "The family kept every letter from the field and read them on the porch. ";

        var allCaps = RenderFullHeaderPdf(Repeat(honors, 600), conflicts, awards);
        var mixed = RenderFullHeaderPdf(Repeat(porch, 600), conflicts, awards);
        AssertHonorsFit(allCaps.Runs);
        AssertHonorsFit(mixed.Runs);
        Assert.Equal("10.4", BioFontSize(allCaps.Pdf));
        Assert.Equal("12", BioFontSize(mixed.Pdf));
        Assert.Contains(mixed.Runs, run => run.Text.Contains(';', StringComparison.Ordinal));
    }

    [Fact]
    public void Three_conflicts_and_ten_awards_with_a_450_character_bio_stay_at_12_point()
    {
        const string conflicts = "World War II, Korean War, Vietnam War";
        const string awards =
            "Purple Heart, Bronze Star, Silver Star, Distinguished Flying Cross, Air Medal, Legion of Merit, Soldier's Medal, Army Commendation Medal, Good Conduct Medal, National Defense Service Medal";
        const string porch = "The family kept every letter from the field and read them on the porch. ";
        var pdf = RenderFullHeaderPdf(Repeat(porch, 450), conflicts, awards);
        var text = string.Join(" ", pdf.Runs.Select(run => run.Text));
        Assert.Contains("Conflicts served", text, StringComparison.Ordinal);
        Assert.Contains("Vietnam War", text, StringComparison.Ordinal);
        Assert.Contains("Awards", text, StringComparison.Ordinal);
        Assert.Contains("National Defense Service Medal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("...", text, StringComparison.Ordinal);
        Assert.Equal("12", BioFontSize(pdf.Pdf));
        AssertAboveContentBottom(pdf.Runs);
    }

    [Fact]
    public void A_legacy_1100_character_bio_fits_whole_without_an_ellipsis()
    {
        const string conflicts = "World War II, Korean War";
        const string awards = "Purple Heart, Bronze Star, Distinguished Flying Cross";
        const string porch = "The family kept every letter from the field and read them on the porch. ";
        var tribute = Repeat(porch, 1100);
        var pdf = RenderFullHeaderPdf(tribute, conflicts, awards);
        var text = string.Join(" ", pdf.Runs.Select(run => run.Text));
        Assert.Contains("Conflicts served", text, StringComparison.Ordinal);
        Assert.Contains("World War II", text, StringComparison.Ordinal);
        Assert.Contains("Awards", text, StringComparison.Ordinal);
        Assert.Contains("Purple Heart", text, StringComparison.Ordinal);
        Assert.DoesNotContain("...", text, StringComparison.Ordinal);
        Assert.Contains(tribute[..24], text, StringComparison.Ordinal);
        AssertAboveContentBottom(pdf.Runs);
        Assert.Equal("8.8", BioFontSize(pdf.Pdf));
    }

    private static string BioFontSize(byte[] pdf)
    {
        var sizes = FontSizes(pdf);
        foreach (var candidate in new[] { "12", "12.0", "12.00", "10.4", "10.40", "9.6", "9.60", "8.8", "8.80", "8.2", "8.20" })
        {
            if (sizes.Contains(candidate))
            {
                return candidate is "12.0" or "12.00" ? "12"
                    : candidate is "10.40" ? "10.4"
                    : candidate is "9.60" ? "9.6"
                    : candidate is "8.80" ? "8.8"
                    : candidate is "8.20" ? "8.2"
                    : candidate;
            }
        }

        return string.Join(", ", sizes);
    }

    [Fact]
    public void A_bio_with_no_conflicts_or_awards_stays_at_12_point()
    {
        var pdf = RenderFullHeaderPdf(Repeat(AllCapsProse, 600), "", "");
        Assert.Contains(FontSizes(pdf.Pdf), size => size is "12" or "12.0" or "12.00");
        Assert.DoesNotContain(FontSizes(pdf.Pdf), size => size is "10.4" or "10.40" or "9.6" or "8.8" or "8.2" or "8.20");
        Assert.DoesNotContain(pdf.Runs, run => run.Text.Contains("Conflicts served", StringComparison.Ordinal));
    }

    [Fact]
    public void A_legacy_bio_that_cannot_fit_keeps_both_sections_and_ends_the_bio_with_an_ellipsis()
    {
        const string conflicts = "World War II, Korean War";
        const string awards = "Purple Heart, Bronze Star, Distinguished Flying Cross";
        var logs = new List<string>();
        var pdf = RenderFullHeaderPdf(new string('W', 20) + " " + Repeat(AllCapsProse, 2000), conflicts, awards, new ListLogger(logs));
        var text = string.Join(" ", pdf.Runs.Select(run => run.Text));
        Assert.Contains("Conflicts served", text, StringComparison.Ordinal);
        Assert.Contains("World War II", text, StringComparison.Ordinal);
        Assert.Contains("Korean War", text, StringComparison.Ordinal);
        Assert.Contains("Awards", text, StringComparison.Ordinal);
        Assert.Contains("Purple Heart", text, StringComparison.Ordinal);
        Assert.Contains("Bronze Star", text, StringComparison.Ordinal);
        Assert.Contains("Distinguished Flying Cross", text, StringComparison.Ordinal);
        Assert.Contains("...", text, StringComparison.Ordinal);
        Assert.Contains(FontSizes(pdf.Pdf), size => size is "8.2" or "8.20");
        AssertAboveContentBottom(pdf.Runs);
        Assert.Contains(logs, line => line.Contains("Honoree", StringComparison.Ordinal) && line.Contains('7'));

        var source = new string('W', 20) + " " + Repeat(AllCapsProse, 2000);
        var words = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ellipsisRun = Assert.Single(pdf.Runs, run => run.Text.Contains("...", StringComparison.Ordinal));
        Assert.EndsWith("...", ellipsisRun.Text, StringComparison.Ordinal);
        var stem = ellipsisRun.Text[..^3].Trim();
        var lastWord = stem.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        Assert.Contains(lastWord, words);
        Assert.True(char.IsLetter(ellipsisRun.Text[^4]), ellipsisRun.Text);
    }

    private static void AssertHonorsFit(IReadOnlyList<TextRun> runs)
    {
        var text = string.Join(" ", runs.Select(run => run.Text));
        Assert.Contains("Conflicts served", text, StringComparison.Ordinal);
        Assert.Contains("World War II", text, StringComparison.Ordinal);
        Assert.Contains("Korean War", text, StringComparison.Ordinal);
        Assert.Contains("Awards", text, StringComparison.Ordinal);
        Assert.Contains("Purple Heart", text, StringComparison.Ordinal);
        Assert.Contains("Bronze Star", text, StringComparison.Ordinal);
        Assert.Contains("Distinguished Flying Cross", text, StringComparison.Ordinal);
        AssertAboveContentBottom(runs);
        Assert.All(runs.Where(run => run.Text is not "AA-40"), run => Assert.InRange(run.X, BioLeft - 1, BioLeft + 180));
    }

    private static void AssertAboveContentBottom(IReadOnlyList<TextRun> runs)
    {
        const double pageHeight = 656;
        const double contentBottom = 578;
        Assert.All(runs, run => Assert.True(run.Y > pageHeight - contentBottom, Dump(runs)));
    }

    private sealed record RenderedCard(byte[] Pdf, List<TextRun> Runs);

    private static RenderedCard RenderFullHeaderPdf(string tribute, string conflicts, string awards, ILogger? logger = null)
    {
        var pdf = HonoreeReportPdfGenerator.Create(
            new EmptyHost(),
            new Honoree
            {
                Id = 7,
                FirstName = "Christopher",
                MiddleName = "Montgomery",
                LastName = "Worthington",
                Nickname = "Kit",
                Rank = "Lieutenant Colonel",
                StartYear = 1942,
                EndYear = 1975,
                Description = tribute,
                ConflictsServed = conflicts,
                Awards = awards,
                ServiceBranch = new ServiceBranch { ServiceBranchName = "United States Army Air Corps" },
                FlagGrid = new FlagGrid { FlagGridName = "AA-40" }
            },
            searchResult: null,
            photoBytes: null,
            SyntheticArtwork(),
            logger);
        return new RenderedCard(pdf, ReadRuns(pdf));
    }

    private sealed class ListLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }

    [Fact]
    public void A_newline_in_the_bio_becomes_a_space_so_the_words_stay_separate()
    {
        foreach (var tribute in new[] { "Alpha\nBeta", "Alpha\r\nBeta", "Alpha\n\n\nBeta", "Alpha \n Beta" })
        {
            var runs = Render(tribute);
            var joined = string.Join(" ", runs.OrderByDescending(run => run.Y).Select(run => run.Text));
            Assert.True(joined.Contains("Alpha Beta", StringComparison.Ordinal), tribute + " => " + Dump(runs));
            Assert.DoesNotContain("AlphaBeta", joined, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Long_bio_keeps_conflicts_and_awards_by_fitting_the_bio()
    {
        var tribute = string.Join(" ", Enumerable.Repeat(
            "The family kept every letter from the field and read them on the porch.",
            40));
        var runs = Render(tribute, conflicts: "Operation Overlord", awards: "Medal of Honor");
        var text = string.Join(" ", runs.Select(run => run.Text));

        Assert.Contains("Conflicts served", text, StringComparison.Ordinal);
        Assert.Contains("Operation Overlord", text, StringComparison.Ordinal);
        Assert.Contains("Awards", text, StringComparison.Ordinal);
        Assert.Contains("Medal of Honor", text, StringComparison.Ordinal);
        AssertAboveContentBottom(runs);
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

    [Fact]
    public void Newline_separated_conflicts_and_awards_each_stay_on_their_own_line()
    {
        const string conflicts =
            "2001-2008 War in Afghanistan\r\n2003-2008 Iraq War\n2007 Intervention in Somalia";
        const string awards =
            "3 Navy Commendation Medals\r5 Navy Achievement Medals; Navy Unit Commendation, Good Conduct Medal";
        var runs = Render("Our Hero!", conflicts, awards);

        foreach (var item in new[]
        {
            "2001-2008 War in Afghanistan",
            "2003-2008 Iraq War",
            "2007 Intervention in Somalia",
            "3 Navy Commendation Medals",
            "5 Navy Achievement Medals",
            "Navy Unit Commendation",
            "Good Conduct Medal"
        })
        {
            var line = Assert.Single(runs, run => run.Text == item);
            Assert.Equal(BioLeft, line.X, 1);
        }

        var joined = string.Join("\n", runs.Select(run => run.Text));
        Assert.DoesNotContain("Afghanistan2003", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("Medals5", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("; ", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Honors_fall_back_to_semicolons_when_one_line_per_item_does_not_fit()
    {
        var items = Enumerable.Range(1, 36).Select(number => $"Campaign {number:00}").ToArray();
        var runs = Render("Our Hero!", string.Join("\n", items), awards: null);
        var body = string.Join(" ", runs.Select(run => run.Text));

        Assert.Contains("Conflicts served", body, StringComparison.Ordinal);
        Assert.Contains("Campaign 01; Campaign 02", body, StringComparison.Ordinal);
        Assert.Contains("Campaign 36", body, StringComparison.Ordinal);
        Assert.DoesNotContain(runs, run => run.Text == "Campaign 01");
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
                Rank = string.Empty,
                ServiceBranch = new ServiceBranch { ServiceBranchName = Branch },
                FlagGrid = new FlagGrid { FlagGridName = "V-12" }
            },
            searchResult: null,
            photoBytes: null,
            SyntheticArtwork());
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        return ReadRuns(pdf);
    }

    private const string AllCapsProse =
        "THE FAMILY KEPT EVERY LETTER FROM THE FIELD AND READ THEM ON THE PORCH. ";

    private const string AllCapsHonors =
        "SERGEANT JAMES ALLEY RECEIVED THE DISTINGUISHED FLYING CROSS, PURPLE HEART, AND BRONZE STAR. CAPTAIN SALVADOR GUZMAN SERVED WITH THE UNITED STATES ARMY AIR CORPS BESIDE LIEUTENANT MARGARET COLE. ";

    [Fact]
    public void Bio_of_exactly_the_limit_in_all_caps_prose_fits_without_shrinking()
    {
        Assert.Equal(0, HonoreeBioLimits.MaxLength % 50);
        Assert.True(HonoreeBioLimits.MaxLength <= 650);

        foreach (var unit in new[] { AllCapsHonors, AllCapsProse })
        {
            var tribute = AllCapsPrefix(unit, HonoreeBioLimits.MaxLength);
            Assert.Equal(HonoreeBioLimits.MaxLength, tribute.Length);
            Assert.False(char.IsWhiteSpace(tribute[0]));
            Assert.False(char.IsWhiteSpace(tribute[^1]));

            var pdf = RenderFullHeaderBytes(tribute);
            var runs = ReadRuns(pdf);
            var bio = BioRuns(runs, tribute);
            Assert.Equal(tribute, string.Join(" ", bio.OrderByDescending(run => run.Y).ThenBy(run => run.X).Select(run => run.Text)));
            Assert.All(bio, run => Assert.Equal(BioLeft, run.X, 1));
            Assert.InRange(bio.Select(run => Math.Round(run.Y, 1)).Distinct().Count(), 1, 24);

            using var measure = NewMeasure();
            var font = new XFont("DejaVu Sans", 12, XFontStyle.Regular);
            Assert.All(bio, run => Assert.True(measure.Gfx.MeasureString(run.Text, font).Width <= 214.5, run.Text));
            Assert.True(MeasureHeight(measure.Gfx, Repeat(unit, HonoreeBioLimits.MaxLength), font) <= 24 * 13.969 + 0.05);

            var sizes = FontSizes(pdf);
            Assert.Contains(sizes, size => size is "12" or "12.0" or "12.00");
            Assert.DoesNotContain(sizes, size => size is "10.4" or "10.40" or "9.6" or "8.8" or "8.2" or "8.20");
        }
    }

    [Fact]
    public void All_caps_honors_one_step_past_the_no_shrink_boundary_exceeds_24_lines()
    {
        using var measure = NewMeasure();
        var font = new XFont("DejaVu Sans", 12, XFontStyle.Regular);
        var atLimit = MeasureHeight(measure.Gfx, Repeat(AllCapsHonors, HonoreeBioLimits.MaxLength), font);
        var atShrinkBoundary = MeasureHeight(measure.Gfx, Repeat(AllCapsHonors, 650), font);
        Assert.True(atLimit <= 24 * 13.969 + 0.05, atLimit.ToString(CultureInfo.InvariantCulture));
        Assert.True(atShrinkBoundary > 24 * 13.969 + 0.05, atShrinkBoundary.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_word_wider_than_the_bio_box_is_hard_wrapped_instead_of_painting_past_it()
    {
        var tribute = new string('W', 40);
        var runs = ReadRuns(RenderFullHeaderBytes(tribute));
        var bio = runs.Where(run => run.Text.Length > 0 && run.Text.All(ch => ch == 'W')).ToList();
        Assert.Equal(tribute.Length, bio.Sum(run => run.Text.Length));
        Assert.True(bio.Count > 1, Dump(runs));
        Assert.All(bio, run => Assert.Equal(BioLeft, run.X, 1));
        using var measure = NewMeasure();
        var font = new XFont("DejaVu Sans", 12, XFontStyle.Regular);
        Assert.All(bio, run => Assert.True(measure.Gfx.MeasureString(run.Text, font).Width <= 214.5, Dump(runs)));
    }

    private static string Repeat(string unit, int length) =>
        string.Concat(Enumerable.Repeat(unit, (length / unit.Length) + 2))[..length];

    private static string AllCapsPrefix(string unit, int length)
    {
        var text = string.Concat(Enumerable.Repeat(unit, (length / unit.Length) + 3));
        var slice = text[..length];
        if (!char.IsWhiteSpace(slice[^1]))
        {
            return slice;
        }

        return slice[..^1] + text[length];
    }

    private static byte[] RenderFullHeaderBytes(string tribute)
    {
        var pdf = HonoreeReportPdfGenerator.Create(
            new EmptyHost(),
            new Honoree
            {
                FirstName = "Salvador",
                LastName = "Guzman",
                Rank = "Sergeant",
                StartYear = 1942,
                EndYear = 1945,
                Description = tribute,
                ServiceBranch = new ServiceBranch { ServiceBranchName = Branch },
                FlagGrid = new FlagGrid { FlagGridName = "V-12" }
            },
            searchResult: null,
            photoBytes: null,
            SyntheticArtwork());
        return pdf;
    }

    private sealed class MeasureSession : IDisposable
    {
        public MeasureSession(PdfDocument document, XGraphics gfx)
        {
            Document = document;
            Gfx = gfx;
        }

        public PdfDocument Document { get; }
        public XGraphics Gfx { get; }

        public void Dispose()
        {
            Gfx.Dispose();
            Document.Dispose();
        }
    }

    private static MeasureSession NewMeasure()
    {
        var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = 294;
        page.Height = 656;
        return new MeasureSession(document, XGraphics.FromPdfPage(page));
    }

    private static double MeasureHeight(XGraphics gfx, string text, XFont font)
    {
        var formatter = new XTextFormatter(gfx)
        {
            AllowVerticalOverflow = true,
            Alignment = XParagraphAlignment.Left
        };
        return formatter.GetLayout(text, font, XBrushes.Black, new XRect(0, 0, 214, 100000)).Height;
    }

    private static List<string> FontSizes(byte[] pdf)
    {
        var content = PageContent(pdf);
        return Regex.Matches(content, @"([\d.]+)\s+Tf")
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    private static string PageContent(byte[] pdf)
    {
        var objects = ReadObjects(pdf);
        var page = objects.Values.Select(Encoding.Latin1.GetString).First(text => text.Contains("/Type /Page\n") || text.Contains("/Type /Page\r"));
        var contentsId = int.Parse(Regex.Match(page, @"/Contents\s+(\d+)\s+0\s+R").Groups[1].Value, CultureInfo.InvariantCulture);
        return Encoding.Latin1.GetString(Inflate(objects[contentsId]));
    }

    [Fact]
    public void Rank_sits_on_its_own_line_between_the_name_and_the_years()
    {
        var runs = RenderHeader("Sergeant");
        var name = Assert.Single(runs, run => run.Text == "Alex Rivera");
        var rank = Assert.Single(runs, run => run.Text == "Sergeant");
        var years = Assert.Single(runs, run => run.Text == "1963 - 1966");
        var branch = Assert.Single(runs, run => run.Text == Branch);

        Assert.True(name.Y > rank.Y && rank.Y > years.Y && years.Y > branch.Y, Dump(runs));
        Assert.Equal(name.Y - rank.Y, rank.Y - years.Y, 0.5);
        Assert.Equal(rank.Y - years.Y, years.Y - branch.Y, 0.5);
        Assert.True(rank.X > BioLeft + 20, Dump(runs));
    }

    [Fact]
    public void Blank_rank_keeps_the_name_directly_above_the_years()
    {
        foreach (var rank in new string?[] { null, "", "   ", "\n\t" })
        {
            var runs = RenderHeader(rank);
            var name = Assert.Single(runs, run => run.Text == "Alex Rivera");
            var years = Assert.Single(runs, run => run.Text == "1963 - 1966");
            var branch = Assert.Single(runs, run => run.Text == Branch);
            Assert.DoesNotContain(runs, run => run.Text == "Sergeant");
            Assert.DoesNotContain(runs, run => run.Y < name.Y - 0.5 && run.Y > years.Y + 0.5);
            Assert.Equal(name.Y - years.Y, years.Y - branch.Y, 0.5);
            Assert.InRange(name.Y - years.Y, 14, 16);
        }
    }

    private static List<TextRun> RenderHeader(string? rank)
    {
        var pdf = HonoreeReportPdfGenerator.Create(
            new EmptyHost(),
            new Honoree
            {
                FirstName = "Alex",
                LastName = "Rivera",
                Rank = rank ?? string.Empty,
                StartYear = 1963,
                EndYear = 1966,
                Description = "Synthetic tribute.",
                ServiceBranch = new ServiceBranch { ServiceBranchName = Branch },
                FlagGrid = new FlagGrid { FlagGridName = "AA-10" }
            },
            searchResult: null,
            photoBytes: null,
            SyntheticArtwork());
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        return ReadRuns(pdf);
    }

    [Fact]
    public void A_card_without_its_background_and_rotary_artwork_is_refused_not_rendered_bare()
    {
        var honoree = new Honoree
        {
            FirstName = "James",
            LastName = "Alley",
            ServiceBranch = new ServiceBranch { ServiceBranchName = Branch },
            FlagGrid = new FlagGrid { FlagGridName = "U-40" }
        };

        var noAssets = Assert.Throws<HonoreeCardArtworkMissingException>(() =>
            HonoreeReportPdfGenerator.Create(new EmptyHost(), honoree, null, null));
        Assert.Equal(["card background DonorCardBlankLrg.png", "Rotary logo Rotary.png"], noAssets.Missing);
        Assert.Contains("servicelogos", noAssets.Message);

        var unreadable = Assert.Throws<HonoreeCardArtworkMissingException>(() =>
            HonoreeReportPdfGenerator.Create(new EmptyHost(), honoree, null, null, new HonoreeReportAssets([1, 2, 3], SyntheticPng(), null)));
        Assert.Equal(["card background DonorCardBlankLrg.png"], unreadable.Missing);

        var pdf = HonoreeReportPdfGenerator.Create(new EmptyHost(), honoree, null, null, SyntheticArtwork());
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(pdf));
    }

    private static HonoreeReportAssets SyntheticArtwork() => new(SyntheticPng(), SyntheticPng(), null);

    private static byte[] SyntheticPng()
    {
        using var bitmap = new SkiaSharp.SKBitmap(4, 4);
        bitmap.Erase(SkiaSharp.SKColors.SteelBlue);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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
