using System.Text;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Drawing.Layout;
using PdfSharpCore.Drawing.Layout.enums;
using PdfSharpCore.Pdf;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed record HonoreeReportAssets(
    byte[]? BackgroundImage,
    byte[]? RotaryImage,
    byte[]? ServiceLogoImage,
    byte[]? SilhouetteImage = null);

public sealed class HonoreeCardArtworkMissingException(IReadOnlyList<string> missing)
    : InvalidOperationException(
        $"The honoree card artwork could not be loaded ({string.Join(", ", missing)}). " +
        "The card was not created, so a card without its background and logos is never saved or printed. " +
        "Check the servicelogos storage container.")
{
    public IReadOnlyList<string> Missing { get; } = missing;
}

public static class HonoreeReportPdfGenerator
{
    private const string FontFamily = "DejaVu Sans";

    public static byte[] Create(
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes,
        HonoreeReportAssets? assets = null,
        ILogger? logger = null)
    {
        using var document = new PdfDocument();
        document.Info.Title = $"{BuildHonoreeName(honoree)} - Plano Flags of Honor";
        document.Info.Author = "Plano Flags of Honor";

        var page = document.AddPage();

        // Legacy honoree card dimensions.
        page.Width = 294;
        page.Height = 656;

        using var gfx = XGraphics.FromPdfPage(page);

        var missing = new List<string>();
        if (!DrawBackground(gfx, page, environment, assets?.BackgroundImage)) missing.Add("card background DonorCardBlankLrg.png");
        DrawFlagGrid(gfx, honoree);
        DrawLogoPhotoRow(gfx, environment, honoree, searchResult, photoBytes, assets?.ServiceLogoImage, assets?.SilhouetteImage);
        DrawCenteredHonoreeContent(gfx, honoree, searchResult, logger);
        if (!DrawRotaryLogo(gfx, environment, assets?.RotaryImage)) missing.Add("Rotary logo Rotary.png");

        // The deploy replaces wwwroot with the frontend build, so the on-disk fallback
        // artwork is absent in production; without this a failed asset load yields a bare card.
        if (missing.Count > 0)
        {
            throw new HonoreeCardArtworkMissingException(missing);
        }

        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static bool DrawBackground(
        XGraphics gfx,
        PdfPage page,
        IWebHostEnvironment environment,
        byte[]? backgroundImage)
    {
        if (TryDrawImageBytes(gfx, backgroundImage, 0, 0, page.Width, page.Height))
        {
            return true;
        }

        var path = FirstExistingAssetPath(environment, "DonorCardBlankLrg.png", "DonorCardBlank.png");

        if (!string.IsNullOrWhiteSpace(path))
        {
            using var background = XImage.FromFile(path);
            gfx.DrawImage(background, 0, 0, page.Width, page.Height);
            return true;
        }

        return false;
    }

    private static void DrawFlagGrid(XGraphics gfx, Honoree honoree)
    {
        var grid = honoree.FlagGrid?.FlagGridName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(grid)) return;

        gfx.DrawString(Clean(grid), Font(8, true), XBrushes.Black, new XPoint(30, 32));
    }

    private static void DrawLogoPhotoRow(
        XGraphics gfx,
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes,
        byte[]? serviceLogoBytes,
        byte[]? silhouetteBytes)
    {
        DrawServiceLogo(gfx, environment, honoree, searchResult, serviceLogoBytes, 27, 52, 72, 72);

        if (ShouldDrawPhoto(honoree, photoBytes))
        {
            var photoHeight = GetPhotoHeight(photoBytes);
            DrawPhoto(gfx, photoBytes, 162, 52, 118, photoHeight);
            return;
        }

        // If no honoree photo exists, show a service-specific silhouette when available.
        // If no service-specific silhouette exists, draw a generic soldier silhouette.
        if (!TryDrawImageBytes(gfx, silhouetteBytes, 176, 52, 88, 112, fitArea: true))
        {
            DrawGenericSoldierSilhouette(gfx, 176, 52, 88, 112);
        }
    }

    private static void DrawServiceLogo(
        XGraphics gfx,
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? serviceLogoBytes,
        double x,
        double y,
        double width,
        double height)
    {
        if (TryDrawImageBytes(gfx, serviceLogoBytes, x, y, width, height, fitArea: true))
        {
            return;
        }

        var logoFile = Clean(honoree.ServiceBranch?.LogoFileName);
        var branchName = honoree.ServiceBranch?.ServiceBranchName ?? searchResult?.ServiceBranchName;

        var path = !string.IsNullOrWhiteSpace(logoFile)
            ? FirstExistingAssetPath(environment, logoFile, Path.ChangeExtension(logoFile, ".png"))
            : null;

        path ??= FirstExistingAssetPath(environment, BranchLogoFile(branchName));

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            using var logo = XImage.FromFile(path);
            DrawImageFitArea(gfx, logo, x, y, width, height);
        }
        catch
        {
            // Keep generating the report even if a logo file is missing/corrupt.
        }
    }

    private static bool ShouldDrawPhoto(Honoree honoree, byte[]? photoBytes)
    {
        if (photoBytes is null || photoBytes.Length == 0)
        {
            return false;
        }

        var photoFileName = Clean(honoree.PhotoFileName);
        return !string.Equals(photoFileName, "silhouette.jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static double GetPhotoHeight(byte[]? photoBytes)
    {
        if (photoBytes is null || photoBytes.Length == 0)
        {
            return 50;
        }

        try
        {
            using var image = XImage.FromStream(() => new MemoryStream(photoBytes));
            var orientation = GetExifOrientation(photoBytes);
            var pixelWidth = OrientationSwapsDimensions(orientation) ? image.PixelHeight : image.PixelWidth;
            var pixelHeight = OrientationSwapsDimensions(orientation) ? image.PixelWidth : image.PixelHeight;

            return pixelWidth > pixelHeight ? 132 : 100;
        }
        catch
        {
            return 50;
        }
    }

    private static void DrawPhoto(XGraphics gfx, byte[]? photoBytes, double x, double y, double width, double height)
    {
        if (photoBytes is null || photoBytes.Length == 0)
        {
            return;
        }

        try
        {
            using var image = XImage.FromStream(() => new MemoryStream(photoBytes));
            DrawImageFitArea(gfx, image, x, y, width, height, GetExifOrientation(photoBytes));
        }
        catch
        {
            // Keep the PDF usable even when a photo format is not supported by PdfSharp.
        }
    }

    private static void DrawGenericSoldierSilhouette(XGraphics gfx, double x, double y, double width, double height)
    {
        var black = XBrushes.Black;
        var centerX = x + (width / 2);

        // Helmet/head.
        gfx.DrawEllipse(black, centerX - 9, y + 4, 18, 18);
        gfx.DrawRectangle(black, centerX - 14, y + 13, 28, 5);

        // Body.
        gfx.DrawPolygon(
            XPens.Black,
            black,
            new[]
            {
                new XPoint(centerX - 13, y + 24),
                new XPoint(centerX + 13, y + 24),
                new XPoint(centerX + 17, y + 54),
                new XPoint(centerX - 17, y + 54)
            },
            XFillMode.Winding);

        // Rifle / diagonal shape.
        var pen = new XPen(XColors.Black, 5);
        gfx.DrawLine(pen, x + 8, y + 18, x + 43, y + 58);

        // Legs.
        gfx.DrawRectangle(black, centerX - 13, y + 52, 10, 18);
        gfx.DrawRectangle(black, centerX + 3, y + 52, 10, 18);
    }

    private static void DrawCenteredHonoreeContent(
        XGraphics gfx,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        ILogger? logger)
    {
        // Matches the legacy QuestPDF card structure, but uses a lighter sans-serif font.
        var y = 178d;

        DrawCenteredText(gfx, BuildLegacyDisplayName(honoree), y, 12.2, bold: true);
        y += 15;

        var rank = Clean(honoree.Rank);
        if (!string.IsNullOrWhiteSpace(rank))
        {
            DrawCenteredText(gfx, rank, y, 12.2, bold: true);
            y += 15;
        }

        var years = BuildServiceYears(honoree);
        if (!string.IsNullOrWhiteSpace(years))
        {
            DrawCenteredText(gfx, years, y, 12.2, bold: true);
            y += 15;
        }

        var serviceBranchName = Clean(honoree.ServiceBranch?.ServiceBranchName ?? searchResult?.ServiceBranchName);
        if (!string.IsNullOrWhiteSpace(serviceBranchName))
        {
            DrawCenteredText(gfx, serviceBranchName, y, 12.2, bold: true);
            y += 23;
        }
        else
        {
            y += 10;
        }

        DrawDescription(gfx, Clean(honoree.Description), y, honoree, logger);
    }

    private static readonly double[] BioFontSizes = [12.0, 10.4, 9.6, 8.8, 8.2];

    private static double LengthBasedFontSize(int length) => length switch
    {
        > 1200 => 8.2,
        > 1000 => 8.8,
        > 800 => 9.6,
        > 650 => 10.4,
        _ => 12.0
    };

    private static void DrawDescription(XGraphics gfx, string description, double y, Honoree honoree, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            description = "No tribute text has been entered for this honoree yet.";
        }

        var lengthSize = LengthBasedFontSize(description.Length);
        if (!HasHonorText(honoree.ConflictsServed) && !HasHonorText(honoree.Awards))
        {
            DrawBioBlock(gfx, description, y, lengthSize, BioBoxHeight);
            return;
        }

        var room = ContentBottom - y;
        foreach (var size in BioFontSizes)
        {
            if (size > lengthSize + 0.001)
            {
                continue;
            }

            if (TryFitBio(gfx, description, honoree, size, room, truncate: false, out var plan))
            {
                DrawBioPlan(gfx, y, plan);
                return;
            }
        }

        if (TryFitBio(gfx, description, honoree, 8.2, room, truncate: true, out var cut))
        {
            DrawBioPlan(gfx, y, cut);
            logger?.LogWarning(
                "Honoree {HonoreeId} bio was cut with an ellipsis on the card so Conflicts served and Awards could fit.",
                honoree.Id);
            return;
        }

        // The honor text itself is taller than the space above the Rotary mark.
        // A section is shown only when the whole section fits, and conflicts win.
        DrawBioBlock(gfx, description, y, lengthSize, Math.Max(lengthSize, room));
        var used = Math.Min(MeasureLeftBlock(gfx, BreakWideWords(gfx, description, Font(lengthSize, false), ContentWidth), Font(lengthSize, false), ContentWidth), Math.Max(lengthSize, room));
        DrawHonors(gfx, honoree, y + used + 10, lengthSize);
    }

    private readonly record struct BioPlan(string Text, double FontSize, double Height, HonorChoice Honors);

    private static bool TryFitBio(
        XGraphics gfx,
        string description,
        Honoree honoree,
        double size,
        double room,
        bool truncate,
        out BioPlan plan)
    {
        plan = default;
        var detail = HonorDetailSize(size);
        var labelFont = Font(detail, true);
        var bodyFont = Font(detail, false);
        var conflicts = HonorForms(gfx, honoree.ConflictsServed, labelFont, bodyFont);
        var awards = HonorForms(gfx, honoree.Awards, labelFont, bodyFont);
        var font = Font(size, false);
        var wrapped = BreakWideWords(gfx, description, font, ContentWidth);
        var height = MeasureLeftBlock(gfx, wrapped, font, ContentWidth);

        if (!truncate)
        {
            var honorBudget = room - 10 - height;
            if (honorBudget <= 0)
            {
                return false;
            }

            var choice = ChooseFittingHonors(conflicts, awards, honorBudget, 8);
            if (choice is null)
            {
                return false;
            }

            plan = new BioPlan(wrapped, size, height, choice.Value);
            return true;
        }

        var shortest = ShortestHonors(conflicts, awards);
        if (shortest is null)
        {
            return false;
        }

        var bioBudget = room - 10 - HonorStackHeight(shortest.Value, 8);
        if (bioBudget < font.GetHeight())
        {
            return false;
        }

        if (height <= bioBudget)
        {
            plan = new BioPlan(wrapped, size, height, shortest.Value);
            return true;
        }

        var cut = FitWithEllipsis(gfx, wrapped, font, bioBudget);
        var cutHeight = MeasureLeftBlock(gfx, cut, font, ContentWidth);
        if (cutHeight > bioBudget)
        {
            return false;
        }

        plan = new BioPlan(cut, size, cutHeight, shortest.Value);
        return true;
    }

    private static HonorChoice? ChooseFittingHonors(
        IReadOnlyList<HonorBlock> conflicts,
        IReadOnlyList<HonorBlock> awards,
        double available,
        double sectionGap)
    {
        if (conflicts.Count > 0 && awards.Count > 0)
        {
            return ChooseBothHonors(conflicts, awards, available, sectionGap);
        }

        return ChooseHonors(conflicts, awards, available, sectionGap);
    }

    private static HonorChoice? ShortestHonors(
        IReadOnlyList<HonorBlock> conflicts,
        IReadOnlyList<HonorBlock> awards)
    {
        if (conflicts.Count > 0 && awards.Count > 0)
        {
            HonorChoice? best = null;
            var bestHeight = double.MaxValue;
            foreach (var conflictsBlock in conflicts)
            {
                foreach (var awardsBlock in awards)
                {
                    var height = conflictsBlock.Height + 8 + awardsBlock.Height;
                    if (height < bestHeight)
                    {
                        bestHeight = height;
                        best = new HonorChoice(conflictsBlock, awardsBlock);
                    }
                }
            }

            return best;
        }

        if (conflicts.Count > 0)
        {
            return new HonorChoice(ShortestBlock(conflicts), null);
        }

        if (awards.Count > 0)
        {
            return new HonorChoice(null, ShortestBlock(awards));
        }

        return null;
    }

    private static HonorBlock ShortestBlock(IReadOnlyList<HonorBlock> blocks)
    {
        var best = blocks[0];
        for (var index = 1; index < blocks.Count; index++)
        {
            if (blocks[index].Height < best.Height)
            {
                best = blocks[index];
            }
        }

        return best;
    }

    private static void DrawBioPlan(XGraphics gfx, double y, BioPlan plan)
    {
        DrawBioText(gfx, plan.Text, y, plan.FontSize, plan.Height);
        DrawChosenHonors(gfx, plan.Honors, y + plan.Height + 10, plan.FontSize);
    }

    private static void DrawBioBlock(XGraphics gfx, string description, double y, double size, double maxHeight)
    {
        var font = Font(size, false);
        var wrapped = BreakWideWords(gfx, description, font, ContentWidth);
        var height = Math.Min(MeasureLeftBlock(gfx, wrapped, font, ContentWidth), maxHeight);
        DrawBioText(gfx, wrapped, y, size, Math.Max(height, font.GetHeight()));
    }

    private static void DrawBioText(XGraphics gfx, string description, double y, double size, double height)
    {
        var font = Font(size, false);
        new XTextFormatter(gfx).DrawString(
            description,
            font,
            XBrushes.Black,
            new XRect(ContentLeft, y, ContentWidth, height),
            new TextFormatAlignment
            {
                Horizontal = XParagraphAlignment.Left,
                Vertical = XVerticalAlignment.Top
            });
    }

    private static string FitWithEllipsis(XGraphics gfx, string text, XFont font, double maxHeight)
    {
        const string ellipsis = "...";
        if (MeasureLeftBlock(gfx, text, font, ContentWidth) <= maxHeight)
        {
            return text;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var best = ellipsis;
        var low = 0;
        var high = words.Length;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var candidate = mid == 0
                ? ellipsis
                : string.Join(' ', words.Take(mid)) + ellipsis;
            if (MeasureLeftBlock(gfx, candidate, font, ContentWidth) <= maxHeight)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static double HonorStackHeight(HonorChoice choice, double sectionGap)
    {
        var height = 0d;
        var sections = 0;
        if (choice.Conflicts is HonorBlock conflicts)
        {
            height += conflicts.Height;
            sections++;
        }

        if (choice.Awards is HonorBlock awards)
        {
            height += awards.Height;
            sections++;
        }

        if (sections == 2)
        {
            height += sectionGap;
        }

        return height;
    }

    private static HonorChoice? ChooseBothHonors(
        IReadOnlyList<HonorBlock> conflicts,
        IReadOnlyList<HonorBlock> awards,
        double available,
        double sectionGap)
    {
        foreach (var conflictsBlock in conflicts)
        {
            foreach (var awardsBlock in awards)
            {
                if (conflictsBlock.Height + sectionGap + awardsBlock.Height <= available)
                {
                    return new HonorChoice(conflictsBlock, awardsBlock);
                }
            }
        }

        return null;
    }

    private static void DrawChosenHonors(XGraphics gfx, HonorChoice choice, double top, double bioSize)
    {
        var detailSize = HonorDetailSize(bioSize);
        var bodyFont = Font(detailSize, false);
        var labelFont = Font(detailSize, true);
        var y = top;
        if (choice.Conflicts is HonorBlock conflictsBlock)
        {
            DrawHonorSection(gfx, "Conflicts served", conflictsBlock.Text, y, labelFont, bodyFont);
            y += conflictsBlock.Height + 8;
        }

        if (choice.Awards is HonorBlock awardsBlock)
        {
            DrawHonorSection(gfx, "Awards", awardsBlock.Text, y, labelFont, bodyFont);
        }
    }

    private static double HonorDetailSize(double bioSize)
    {
        var detailSize = Math.Min(9, bioSize - 1.5);
        if (detailSize >= bioSize)
        {
            detailSize = bioSize - 0.5;
        }

        return detailSize;
    }

    private static bool HasHonorText(string? value) => SplitHonorItems(value).Count > 0;

    private const double ContentLeft = 40;
    private const double ContentWidth = 214;
    private const double BioBoxHeight = 345;
    // Stay above the Rotary mark drawn at y=596.
    private const double ContentBottom = 578;

    private readonly record struct HonorBlock(string Text, double Height);

    private static void DrawHonors(XGraphics gfx, Honoree honoree, double top, double bioSize)
    {
        var detailSize = Math.Min(9, bioSize - 1.5);
        if (detailSize >= bioSize)
        {
            detailSize = bioSize - 0.5;
        }

        var bodyFont = Font(detailSize, false);
        var labelFont = Font(detailSize, true);
        var available = ContentBottom - top;
        if (available <= 0)
        {
            return;
        }

        const double sectionGap = 8;
        var conflicts = HonorForms(gfx, honoree.ConflictsServed, labelFont, bodyFont);
        var awards = HonorForms(gfx, honoree.Awards, labelFont, bodyFont);
        var choice = ChooseHonors(conflicts, awards, available, sectionGap);
        if (choice is null)
        {
            return;
        }

        var y = top;
        if (choice.Value.Conflicts is HonorBlock conflictsBlock)
        {
            DrawHonorSection(gfx, "Conflicts served", conflictsBlock.Text, y, labelFont, bodyFont);
            y += conflictsBlock.Height + sectionGap;
        }

        if (choice.Value.Awards is HonorBlock awardsBlock)
        {
            DrawHonorSection(gfx, "Awards", awardsBlock.Text, y, labelFont, bodyFont);
        }
    }

    private readonly record struct HonorChoice(HonorBlock? Conflicts, HonorBlock? Awards);

    private static List<HonorBlock> HonorForms(XGraphics gfx, string? raw, XFont labelFont, XFont bodyFont)
    {
        var items = SplitHonorItems(raw);
        if (items.Count == 0)
        {
            return [];
        }

        var forms = new List<HonorBlock>
        {
            new(string.Join("\n", items), SectionHeight(gfx, string.Join("\n", items), labelFont, bodyFont))
        };
        if (items.Count > 1)
        {
            var compact = string.Join("; ", items);
            forms.Add(new HonorBlock(compact, SectionHeight(gfx, compact, labelFont, bodyFont)));
        }

        return forms;
    }

    private static HonorChoice? ChooseHonors(
        IReadOnlyList<HonorBlock> conflicts,
        IReadOnlyList<HonorBlock> awards,
        double available,
        double sectionGap)
    {
        if (conflicts.Count > 0 && awards.Count > 0)
        {
            foreach (var conflictsBlock in conflicts)
            {
                foreach (var awardsBlock in awards)
                {
                    if (conflictsBlock.Height + sectionGap + awardsBlock.Height <= available)
                    {
                        return new HonorChoice(conflictsBlock, awardsBlock);
                    }
                }
            }
        }

        foreach (var conflictsBlock in conflicts)
        {
            if (conflictsBlock.Height <= available)
            {
                return new HonorChoice(conflictsBlock, null);
            }
        }

        foreach (var awardsBlock in awards)
        {
            if (awardsBlock.Height <= available)
            {
                return new HonorChoice(null, awardsBlock);
            }
        }

        return null;
    }

    private static List<string> SplitHonorItems(string? value)
    {
        var items = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return items;
        }

        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var line in text.Split('\n'))
        {
            foreach (var piece in line.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (piece.Length > 0)
                {
                    items.Add(piece);
                }
            }
        }

        return items;
    }

    private static double SectionHeight(XGraphics gfx, string value, XFont labelFont, XFont bodyFont)
    {
        var bodyHeight = value.Split('\n').Sum(line => MeasureLeftBlock(gfx, line, bodyFont, ContentWidth));
        return labelFont.GetHeight() + 2 + bodyHeight;
    }

    private static void DrawHonorSection(
        XGraphics gfx,
        string label,
        string value,
        double y,
        XFont labelFont,
        XFont bodyFont)
    {
        var labelHeight = labelFont.GetHeight();
        gfx.DrawString(
            label,
            labelFont,
            XBrushes.Black,
            new XRect(ContentLeft, y, ContentWidth, labelHeight + 1),
            XStringFormats.TopLeft);
        var lineY = y + labelHeight + 2;
        foreach (var line in value.Split('\n'))
        {
            var lineHeight = MeasureLeftBlock(gfx, line, bodyFont, ContentWidth);
            new XTextFormatter(gfx).DrawString(
                line,
                bodyFont,
                XBrushes.Black,
                new XRect(ContentLeft, lineY, ContentWidth, lineHeight + bodyFont.GetHeight()),
                new TextFormatAlignment
                {
                    Horizontal = XParagraphAlignment.Left,
                    Vertical = XVerticalAlignment.Top
                });
            lineY += lineHeight;
        }
    }

    private static string BreakWideWords(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var builder = new StringBuilder(text.Length);
        var changed = false;
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                builder.Append(text[index]);
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var word = text.AsSpan(start, index - start);
            if (gfx.MeasureString(word.ToString(), font).Width <= maxWidth)
            {
                builder.Append(word);
                continue;
            }

            changed = true;
            var offset = 0;
            while (offset < word.Length)
            {
                var count = FittedPrefix(gfx, font, word[offset..], maxWidth);
                if (offset > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(word.Slice(offset, count));
                offset += count;
            }
        }

        return changed ? builder.ToString() : text;
    }

    private static int FittedPrefix(XGraphics gfx, XFont font, ReadOnlySpan<char> word, double maxWidth)
    {
        if (word.Length <= 1)
        {
            return Math.Max(word.Length, 1);
        }

        var low = 1;
        var high = word.Length;
        var fit = 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (gfx.MeasureString(word[..mid].ToString(), font).Width <= maxWidth)
            {
                fit = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return fit;
    }

    private static double MeasureLeftBlock(XGraphics gfx, string text, XFont font, double width)
    {
        var formatter = new XTextFormatter(gfx)
        {
            AllowVerticalOverflow = true,
            Alignment = XParagraphAlignment.Left
        };
        var layout = formatter.GetLayout(text, font, XBrushes.Black, new XRect(0, 0, width, 100000));
        return Math.Max(layout.Height, font.GetHeight());
    }

    private static bool DrawRotaryLogo(XGraphics gfx, IWebHostEnvironment environment, byte[]? rotaryImage)
    {
        if (TryDrawImageBytes(gfx, rotaryImage, 234, 596, 50, 50, fitArea: true))
        {
            return true;
        }

        var path = FirstExistingAssetPath(environment, "Rotary.png", "Rotary.jpg");
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            using var rotary = XImage.FromFile(path);
            DrawImageFitArea(gfx, rotary, 234, 596, 50, 50);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DrawCenteredText(XGraphics gfx, string text, double y, double size, bool bold = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        gfx.DrawString(
            text,
            Font(size, bold),
            XBrushes.Black,
            new XRect(40, y, 214, 18),
            CenterFormat());
    }

    private static bool TryDrawImageBytes(
        XGraphics gfx,
        byte[]? imageBytes,
        double x,
        double y,
        double width,
        double height,
        bool fitArea = false)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return false;
        }

        try
        {
            using var image = XImage.FromStream(() => new MemoryStream(imageBytes));

            if (fitArea)
            {
                DrawImageFitArea(gfx, image, x, y, width, height);
            }
            else
            {
                gfx.DrawImage(image, x, y, width, height);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DrawImageFitArea(
        XGraphics gfx,
        XImage image,
        double x,
        double y,
        double width,
        double height,
        int orientation = 1)
    {
        var pixelWidth = OrientationSwapsDimensions(orientation) ? image.PixelHeight : image.PixelWidth;
        var pixelHeight = OrientationSwapsDimensions(orientation) ? image.PixelWidth : image.PixelHeight;
        var imageRatio = pixelWidth / (double)Math.Max(pixelHeight, 1);
        var boxRatio = width / height;

        double drawWidth;
        double drawHeight;

        if (imageRatio > boxRatio)
        {
            drawWidth = width;
            drawHeight = width / imageRatio;
        }
        else
        {
            drawHeight = height;
            drawWidth = height * imageRatio;
        }

        var drawX = x + ((width - drawWidth) / 2);
        var drawY = y + ((height - drawHeight) / 2);

        DrawImageWithOrientation(gfx, image, drawX, drawY, drawWidth, drawHeight, orientation);
    }

    private static void DrawImageWithOrientation(
        XGraphics gfx,
        XImage image,
        double x,
        double y,
        double width,
        double height,
        int orientation)
    {
        switch (orientation)
        {
            case 2:
                var state2 = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x + width, y);
                    gfx.ScaleTransform(-1, 1);
                    gfx.DrawImage(image, 0, 0, width, height);
                }
                finally
                {
                    gfx.Restore(state2);
                }
                return;

            case 3:
                var state3 = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x + (width / 2), y + (height / 2));
                    gfx.RotateTransform(180);
                    gfx.DrawImage(image, -width / 2, -height / 2, width, height);
                }
                finally
                {
                    gfx.Restore(state3);
                }
                return;

            case 4:
                var state4 = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x, y + height);
                    gfx.ScaleTransform(1, -1);
                    gfx.DrawImage(image, 0, 0, width, height);
                }
                finally
                {
                    gfx.Restore(state4);
                }
                return;

            case 5:
                var state5 = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x + (width / 2), y + (height / 2));
                    gfx.RotateTransform(90);
                    gfx.ScaleTransform(-1, 1);
                    gfx.DrawImage(image, -height / 2, -width / 2, height, width);
                }
                finally
                {
                    gfx.Restore(state5);
                }
                return;

            case 6:
                var state6 = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x + (width / 2), y + (height / 2));
                    gfx.RotateTransform(90);
                    gfx.DrawImage(image, -height / 2, -width / 2, height, width);
                }
                finally
                {
                    gfx.Restore(state6);
                }
                return;

            case 7:
                var state7 = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x + (width / 2), y + (height / 2));
                    gfx.RotateTransform(-90);
                    gfx.ScaleTransform(-1, 1);
                    gfx.DrawImage(image, -height / 2, -width / 2, height, width);
                }
                finally
                {
                    gfx.Restore(state7);
                }
                return;

            case 8:
                var state8 = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x + (width / 2), y + (height / 2));
                    gfx.RotateTransform(-90);
                    gfx.DrawImage(image, -height / 2, -width / 2, height, width);
                }
                finally
                {
                    gfx.Restore(state8);
                }
                return;

            default:
                gfx.DrawImage(image, x, y, width, height);
                return;
        }
    }

    private static bool OrientationSwapsDimensions(int orientation) =>
        orientation is 5 or 6 or 7 or 8;

    private static int GetExifOrientation(byte[]? imageBytes)
    {
        if (imageBytes is null || imageBytes.Length < 12)
        {
            return 1;
        }

        // EXIF orientation is normally stored in JPEG APP1 metadata. Phones often
        // save portrait photos sideways in the pixels and use this value to tell
        // viewers how to rotate the image.
        if (imageBytes[0] != 0xFF || imageBytes[1] != 0xD8)
        {
            return 1;
        }

        var offset = 2;

        while (offset + 4 < imageBytes.Length)
        {
            if (imageBytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = imageBytes[offset + 1];
            offset += 2;

            if (marker is 0xDA or 0xD9)
            {
                break;
            }

            if (offset + 2 > imageBytes.Length)
            {
                break;
            }

            var segmentLength = ReadUInt16BigEndian(imageBytes, offset);
            if (segmentLength < 2)
            {
                break;
            }

            var segmentStart = offset + 2;
            var segmentDataLength = segmentLength - 2;

            if (segmentStart + segmentDataLength > imageBytes.Length)
            {
                break;
            }

            if (marker == 0xE1 &&
                segmentDataLength >= 14 &&
                imageBytes[segmentStart] == (byte)'E' &&
                imageBytes[segmentStart + 1] == (byte)'x' &&
                imageBytes[segmentStart + 2] == (byte)'i' &&
                imageBytes[segmentStart + 3] == (byte)'f' &&
                imageBytes[segmentStart + 4] == 0 &&
                imageBytes[segmentStart + 5] == 0)
            {
                return ReadTiffOrientation(imageBytes, segmentStart + 6, segmentDataLength - 6);
            }

            offset += segmentLength;
        }

        return 1;
    }

    private static int ReadTiffOrientation(byte[] bytes, int tiffStart, int tiffLength)
    {
        if (tiffLength < 8 || tiffStart + tiffLength > bytes.Length)
        {
            return 1;
        }

        var littleEndian = bytes[tiffStart] == (byte)'I' && bytes[tiffStart + 1] == (byte)'I';
        var bigEndian = bytes[tiffStart] == (byte)'M' && bytes[tiffStart + 1] == (byte)'M';

        if (!littleEndian && !bigEndian)
        {
            return 1;
        }

        var magic = ReadUInt16(bytes, tiffStart + 2, littleEndian);
        if (magic != 42)
        {
            return 1;
        }

        var ifdOffset = ReadUInt32(bytes, tiffStart + 4, littleEndian);
        if (ifdOffset > int.MaxValue)
        {
            return 1;
        }

        var ifdStart = tiffStart + (int)ifdOffset;
        if (ifdStart < tiffStart || ifdStart + 2 > tiffStart + tiffLength)
        {
            return 1;
        }

        var entryCount = ReadUInt16(bytes, ifdStart, littleEndian);
        var entryStart = ifdStart + 2;

        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = entryStart + (i * 12);
            if (entryOffset + 12 > tiffStart + tiffLength)
            {
                break;
            }

            var tag = ReadUInt16(bytes, entryOffset, littleEndian);
            if (tag != 0x0112)
            {
                continue;
            }

            var type = ReadUInt16(bytes, entryOffset + 2, littleEndian);
            var count = ReadUInt32(bytes, entryOffset + 4, littleEndian);

            if (type == 3 && count >= 1)
            {
                var orientation = ReadUInt16(bytes, entryOffset + 8, littleEndian);
                return orientation is >= 1 and <= 8 ? orientation : 1;
            }
        }

        return 1;
    }

    private static ushort ReadUInt16BigEndian(byte[] bytes, int offset)
    {
        return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }

    private static ushort ReadUInt16(byte[] bytes, int offset, bool littleEndian)
    {
        if (littleEndian)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }

    private static uint ReadUInt32(byte[] bytes, int offset, bool littleEndian)
    {
        if (littleEndian)
        {
            return (uint)(
                bytes[offset] |
                (bytes[offset + 1] << 8) |
                (bytes[offset + 2] << 16) |
                (bytes[offset + 3] << 24));
        }

        return (uint)(
            (bytes[offset] << 24) |
            (bytes[offset + 1] << 16) |
            (bytes[offset + 2] << 8) |
            bytes[offset + 3]);
    }

    private static string? FirstExistingAssetPath(IWebHostEnvironment environment, params string?[] fileNames)
    {
        var roots = new[]
        {
            Path.Combine(environment.WebRootPath ?? AppContext.BaseDirectory, "assets"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "assets"),
            Path.Combine(AppContext.BaseDirectory, "assets")
        };

        foreach (var fileName in fileNames)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            foreach (var root in roots)
            {
                var path = Path.Combine(root, fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static XStringFormat CenterFormat() => new()
    {
        Alignment = XStringAlignment.Center,
        LineAlignment = XLineAlignment.Near
    };

    private static XFont Font(double size, bool bold = false)
    {
        // DejaVu Sans is commonly present on Linux App Service and is closer to
        // the lighter sans-serif look from the legacy QuestPDF output than the
        // current serif-looking fallback.
        return new XFont(FontFamily, size, bold ? XFontStyle.Bold : XFontStyle.Regular);
    }

    private static string BuildLegacyDisplayName(Honoree honoree)
    {
        return AddNickname(
            string.Join(" ", new[] { honoree.FirstName, honoree.LastName }
                .Where(v => !string.IsNullOrWhiteSpace(v))),
            honoree.Nickname);
    }

    private static string BuildHonoreeName(Honoree honoree)
    {
        return AddNickname(
            string.Join(" ", new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
                .Where(v => !string.IsNullOrWhiteSpace(v))),
            honoree.Nickname);
    }

    private static string AddNickname(string name, string? nickname)
    {
        var cleanName = Clean(name);
        var cleanNickname = Clean(nickname);

        if (string.IsNullOrWhiteSpace(cleanNickname))
        {
            return cleanName;
        }

        return cleanName.EndsWith($"({cleanNickname})", StringComparison.OrdinalIgnoreCase)
            ? cleanName
            : $"{cleanName} ({cleanNickname})";
    }

    private static string BuildServiceYears(Honoree honoree)
    {
        if (honoree.StartYear.HasValue && honoree.StartYear.Value > 0 &&
            honoree.EndYear.HasValue && honoree.EndYear.Value > 0)
        {
            return $"{honoree.StartYear} - {honoree.EndYear}";
        }

        if (honoree.StartYear.HasValue && honoree.StartYear.Value > 0 &&
            (!honoree.EndYear.HasValue || honoree.EndYear.Value == 0))
        {
            return $"{honoree.StartYear} - Present";
        }

        if ((!honoree.StartYear.HasValue || honoree.StartYear.Value == 0) &&
            honoree.EndYear.HasValue && honoree.EndYear.Value > 0)
        {
            return $"- {honoree.EndYear}";
        }

        return string.Empty;
    }

    private static string BranchLogoFile(string? branchName)
    {
        var value = branchName?.ToLowerInvariant() ?? string.Empty;

        if (value.Contains("marine corps")) return "MarineCorps.png";
        if (value.Contains("merchant")) return "MerchantMarine.png";
        if (value.Contains("coast guard")) return "CoastGuard.png";
        if (value.Contains("space")) return "SpaceForce.png";
        if (value.Contains("air force")) return "AirForce.png";
        if (value.Contains("army air")) return "ArmyAirCorps.png";
        if (value.Contains("national guard")) return "NationalGuard.png";
        if (value.Contains("signal")) return "SignalCorps.png";
        if (value.Contains("cavalry")) return "Cavalry.png";
        if (value.Contains("navy")) return "Navy.png";
        if (value.Contains("army")) return "Army.png";

        return "MilitaryService.png";
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // A newline is a word break. Dropping it with no space glues the words
        // together. A run of newlines, and the spaces beside that run, become one space.
        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        var afterBreak = false;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                afterBreak = true;
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (afterBreak && ch is ' ' or '\t')
            {
                continue;
            }

            if (afterBreak)
            {
                afterBreak = false;
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}
