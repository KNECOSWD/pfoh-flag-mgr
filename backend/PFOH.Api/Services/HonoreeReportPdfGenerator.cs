using PdfSharpCore.Drawing;
using PdfSharpCore.Drawing.Layout;
using PdfSharpCore.Pdf;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed record HonoreeReportAssets(
    byte[]? BackgroundImage,
    byte[]? RotaryImage,
    byte[]? ServiceLogoImage,
    byte[]? SilhouetteImage = null);

public static class HonoreeReportPdfGenerator
{
    private const string FontFamily = "DejaVu Sans";

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

        // Legacy honoree card dimensions.
        page.Width = 294;
        page.Height = 656;

        using var gfx = XGraphics.FromPdfPage(page);

        DrawBackground(gfx, page, environment, assets?.BackgroundImage);
        DrawFlagGrid(gfx, honoree);
        DrawLogoPhotoRow(gfx, environment, honoree, searchResult, photoBytes, assets?.ServiceLogoImage, assets?.SilhouetteImage);
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

        var path = FirstExistingAssetPath(environment, "DonorCardBlankLrg.png", "DonorCardBlank.png");

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
            return image.PixelWidth > image.PixelHeight ? 132 : 100;
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
        HonoreeSearchResult? searchResult)
    {
        // Matches the legacy QuestPDF card structure, but uses a lighter sans-serif font.
        var y = 178d;

        DrawCenteredText(gfx, BuildLegacyDisplayName(honoree), y, 12.2, bold: true);
        y += 15;

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
            > 1200 => 8.2,
            > 1000 => 8.8,
            > 800 => 9.6,
            > 650 => 10.4,
            _ => 12.0
        };

        var rect = new XRect(40, y, 214, 345);
        var formatter = new XTextFormatter(gfx)
        {
            Alignment = XParagraphAlignment.Center
        };

        formatter.DrawString(description, Font(fontSize, false), XBrushes.Black, rect);
    }

    private static void DrawRotaryLogo(XGraphics gfx, IWebHostEnvironment environment, byte[]? rotaryImage)
    {
        if (TryDrawImageBytes(gfx, rotaryImage, 234, 596, 50, 50, fitArea: true))
        {
            return;
        }

        var path = FirstExistingAssetPath(environment, "Rotary.png", "Rotary.jpg");
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
        // DejaVu Sans is commonly present on Linux App Service and is closer to
        // the lighter sans-serif look from the legacy QuestPDF output than the
        // current serif-looking fallback.
        return new XFont(FontFamily, size, bold ? XFontStyle.Bold : XFontStyle.Regular);
    }

    private static string BuildLegacyDisplayName(Honoree honoree)
    {
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
        if (value.Contains("national guard")) return "NationalGuard.png";
        if (value.Contains("signal")) return "SignalCorps.png";
        if (value.Contains("cavalry")) return "Cavalry.png";
        if (value.Contains("navy")) return "Navy.png";
        if (value.Contains("army")) return "Army.png";

        return "MilitaryService.png";
    }

    private static string Clean(string? value) => (value ?? string.Empty).Replace("\r", "").Replace("\n", "").Trim();
}
