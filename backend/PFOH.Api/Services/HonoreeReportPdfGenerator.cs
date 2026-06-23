using PdfSharpCore.Drawing;
using PdfSharpCore.Drawing.Layout;
using PdfSharpCore.Pdf;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed record HonoreeReportAssets(
    byte[]? BackgroundImage,
    byte[]? RotaryImage,
    byte[]? ServiceLogoImage);

public static class HonoreeReportPdfGenerator
{
    public static byte[] Create(
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes,
        HonoreeReportAssets? assets = null)
    {
        using var document = new PdfDocument();
        document.Info.Title = $"{BuildHonoreeName(honoree)} - Plano Flags of Honor";
        document.Info.Author = "Plano Flags of Honor";

        var page = document.AddPage();

        // Match the legacy QuestPDF card dimensions.
        page.Width = 294;
        page.Height = 656;

        using var gfx = XGraphics.FromPdfPage(page);

        DrawBackground(gfx, page, environment, assets?.BackgroundImage);
        DrawFlagGrid(gfx, honoree);
        DrawLogoPhotoRow(gfx, environment, honoree, searchResult, photoBytes, assets?.ServiceLogoImage);
        DrawCenteredHonoreeContent(gfx, honoree, searchResult);
        DrawRotaryLogo(gfx, environment, assets?.RotaryImage);

        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static void DrawBackground(
        XGraphics gfx,
        PdfPage page,
        IWebHostEnvironment environment,
        byte[]? backgroundImage)
    {
        if (TryDrawImageBytes(gfx, backgroundImage, 0, 0, page.Width, page.Height))
        {
            return;
        }

        // Legacy code used DonorCardBlank.png from the servicelogos container.
        // Prefer that exact filename when present; fall back to the current large asset.
        var path = FirstExistingAssetPath(environment, "DonorCardBlank.png", "DonorCardBlankLrg.png");

        if (!string.IsNullOrWhiteSpace(path))
        {
            using var background = XImage.FromFile(path);
            gfx.DrawImage(background, 0, 0, page.Width, page.Height);
            return;
        }

        gfx.DrawRectangle(XBrushes.White, 0, 0, page.Width, page.Height);
    }

    private static void DrawFlagGrid(XGraphics gfx, Honoree honoree)
    {
        var grid = honoree.FlagGrid?.FlagGridName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(grid)) return;

        // Legacy layout:
        // column.Item().AlignLeft().PaddingTop(25).PaddingLeft(30).Text(grid).FontSize(8)
        gfx.DrawString(Clean(grid), Font(8), XBrushes.Black, new XPoint(30, 32));
    }

    private static void DrawLogoPhotoRow(
        XGraphics gfx,
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes,
        byte[]? serviceLogoBytes)
    {
        // Legacy layout:
        // column.Item().PaddingTop(10).Row(...)
        // service image: AlignLeft().PaddingLeft(30).Width(50).Height(50)
        // honoree image: AlignRight().PaddingRight(30).Height(50 or 100)
        DrawServiceLogo(gfx, environment, honoree, searchResult, serviceLogoBytes, 30, 54, 50, 50);

        if (ShouldDrawPhoto(honoree, photoBytes))
        {
            var photoHeight = GetPhotoHeight(photoBytes);
            DrawPhoto(gfx, photoBytes, 166, 54, 98, photoHeight);
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
            // Keep generating the honoree report even if a logo file is missing/corrupt.
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
            return image.PixelWidth > image.PixelHeight ? 100 : 50;
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
            DrawImageFitArea(gfx, image, x, y, width, height);
        }
        catch
        {
            // Keep the PDF usable even when a photo format is not supported by PdfSharp.
        }
    }

    private static void DrawCenteredHonoreeContent(
        XGraphics gfx,
        Honoree honoree,
        HonoreeSearchResult? searchResult)
    {
        // Legacy layout:
        // AlignCenter().PaddingLeft(40).PaddingRight(40).PaddingTop(20)
        // First Last, years, branch, then description with PaddingTop(10), all 12pt.
        var y = 132d;

        DrawCenteredText(gfx, BuildLegacyDisplayName(honoree), y, 12, bold: true);
        y += 15;

        var years = BuildServiceYears(honoree);
        if (!string.IsNullOrWhiteSpace(years))
        {
            DrawCenteredText(gfx, years, y, 12, bold: true);
            y += 15;
        }

        var serviceBranchName = Clean(honoree.ServiceBranch?.ServiceBranchName ?? searchResult?.ServiceBranchName);
        if (!string.IsNullOrWhiteSpace(serviceBranchName))
        {
            DrawCenteredText(gfx, serviceBranchName, y, 12, bold: true);
            y += 22;
        }
        else
        {
            y += 10;
        }

        DrawDescription(gfx, Clean(honoree.Description), y);
    }

    private static void DrawDescription(XGraphics gfx, string description, double y)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            description = "No tribute text has been entered for this honoree yet.";
        }

        var fontSize = description.Length switch
        {
            > 1100 => 8.4,
            > 900 => 9.2,
            > 700 => 10.0,
            _ => 12.0
        };

        var rect = new XRect(40, y, 214, 345);
        var formatter = new XTextFormatter(gfx)
        {
            Alignment = XParagraphAlignment.Center
        };

        formatter.DrawString(description, Font(fontSize), XBrushes.Black, rect);
    }

    private static void DrawRotaryLogo(XGraphics gfx, IWebHostEnvironment environment, byte[]? rotaryImage)
    {
        // Legacy layout:
        // PaddingBottom(10).PaddingRight(10).AlignRight().AlignBottom().Width(50).Height(50)
        if (TryDrawImageBytes(gfx, rotaryImage, 234, 596, 50, 50, fitArea: true))
        {
            return;
        }

        var path = FirstExistingAssetPath(environment, "Rotary.png");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            using var rotary = XImage.FromFile(path);
            DrawImageFitArea(gfx, rotary, 234, 596, 50, 50);
        }
        catch
        {
            // Keep generating the PDF even if the Rotary image is missing/corrupt.
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
        double height)
    {
        var imageRatio = image.PixelWidth / (double)Math.Max(image.PixelHeight, 1);
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

        gfx.DrawImage(image, drawX, drawY, drawWidth, drawHeight);
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
        return new XFont("Helvetica", size, bold ? XFontStyle.Bold : XFontStyle.Regular);
    }

    private static string BuildLegacyDisplayName(Honoree honoree)
    {
        // Legacy code used FirstName + LastName only.
        return string.Join(" ", new[] { honoree.FirstName, honoree.LastName }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static string BuildHonoreeName(Honoree honoree)
    {
        return string.Join(" ", new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
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
        if (value.Contains("national guard")) return "ArmyNationalGuard.png";
        if (value.Contains("signal")) return "SignalCorps.png";
        if (value.Contains("cavalry")) return "Cavalry.png";
        if (value.Contains("navy")) return "Navy.png";
        if (value.Contains("army")) return "Army.png";

        return "MilitaryService.png";
    }

    private static string Clean(string? value) => (value ?? string.Empty).Replace("\r", "").Replace("\n", "").Trim();
}
