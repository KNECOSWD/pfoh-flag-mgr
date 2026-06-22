using PdfSharpCore.Drawing;
using PdfSharpCore.Drawing.Layout;
using PdfSharpCore.Pdf;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public static class HonoreeReportPdfGenerator
{
    public static byte[] Create(
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes)
    {
        using var document = new PdfDocument();
        document.Info.Title = $"{BuildHonoreeName(honoree)} - Plano Flags of Honor";
        document.Info.Author = "Plano Flags of Honor";

        var page = document.AddPage();
        page.Width = 294;
        page.Height = 656;

        using var gfx = XGraphics.FromPdfPage(page);

        DrawBackground(gfx, page, environment);
        DrawGridCode(gfx, honoree);
        DrawBranchLogo(gfx, environment, honoree.ServiceBranch?.ServiceBranchName ?? searchResult?.ServiceBranchName);
        DrawPhoto(gfx, photoBytes);
        DrawHeader(gfx, honoree, searchResult);
        DrawTribute(gfx, honoree);
        DrawRotaryLogo(gfx, environment);

        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static void DrawBackground(XGraphics gfx, PdfPage page, IWebHostEnvironment environment)
    {
        var path = AssetPath(environment, "DonorCardBlankLrg.png");
        if (File.Exists(path))
        {
            using var background = XImage.FromFile(path);
            gfx.DrawImage(background, 0, 0, page.Width, page.Height);
            return;
        }

        gfx.DrawRectangle(XBrushes.White, 0, 0, page.Width, page.Height);
    }

    private static void DrawGridCode(XGraphics gfx, Honoree honoree)
    {
        var grid = honoree.FlagGrid?.FlagGridName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(grid)) return;

        gfx.DrawString(grid, Font(8), XBrushes.Black, new XPoint(30, 32));
    }

    private static void DrawBranchLogo(XGraphics gfx, IWebHostEnvironment environment, string? branchName)
    {
        var logoFile = BranchLogoFile(branchName);
        var path = AssetPath(environment, logoFile);
        const double x = 34;
        const double y = 54;
        const double size = 54;

        if (File.Exists(path))
        {
            using var logo = XImage.FromFile(path);
            gfx.DrawImage(logo, x, y, size, size);
            return;
        }

        gfx.DrawEllipse(XBrushes.White, x, y, size, size);
        gfx.DrawString("PFOH", Font(9, true), XBrushes.Black, new XRect(x, y + 20, size, 15), CenterFormat());
    }

    private static void DrawPhoto(XGraphics gfx, byte[]? photoBytes)
    {
        const double x = 162;
        const double y = 50;
        const double width = 98;
        const double height = 80;

        if (photoBytes is null || photoBytes.Length == 0)
        {
            return;
        }

        try
        {
            using var image = XImage.FromStream(() => new MemoryStream(photoBytes));
            DrawImageContained(gfx, image, x, y, width, height);
        }
        catch
        {
            // Keep the PDF usable even if the image type is not supported by PdfSharp.
        }
    }

    private static void DrawImageContained(XGraphics gfx, XImage image, double x, double y, double width, double height)
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

    private static void DrawHeader(XGraphics gfx, Honoree honoree, HonoreeSearchResult? searchResult)
    {
        var y = 168d;
        DrawCentered(gfx, BuildHonoreeName(honoree), y, 12.5, true);
        y += 14;

        var years = BuildServiceYears(honoree);
        if (!string.IsNullOrWhiteSpace(years))
        {
            DrawCentered(gfx, years, y, 12, true);
            y += 14;
        }

        var branch = honoree.ServiceBranch?.ServiceBranchName ?? searchResult?.ServiceBranchName;
        if (!string.IsNullOrWhiteSpace(branch))
        {
            DrawCentered(gfx, branch, y, 12, true);
        }
    }

    private static void DrawTribute(XGraphics gfx, Honoree honoree)
    {
        var description = honoree.Description?.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            description = "No tribute text has been entered for this honoree yet.";
        }

        var fontSize = description.Length switch
        {
            > 950 => 8.7,
            > 760 => 9.5,
            > 580 => 10.5,
            _ => 11.5
        };

        var rect = new XRect(40, 222, 214, 245);
        var formatter = new XTextFormatter(gfx)
        {
            Alignment = XParagraphAlignment.Left
        };

        formatter.DrawString(description, Font(fontSize), XBrushes.Black, rect);
    }

    private static void DrawRotaryLogo(XGraphics gfx, IWebHostEnvironment environment)
    {
        var path = AssetPath(environment, "Rotary.png");
        if (!File.Exists(path)) return;

        using var rotary = XImage.FromFile(path);
        gfx.DrawImage(rotary, 224, 586, 45, 45);
    }

    private static void DrawCentered(XGraphics gfx, string text, double y, double size, bool bold = false)
    {
        gfx.DrawString(text, Font(size, bold), XBrushes.Black, new XRect(0, y, 294, 18), CenterFormat());
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

    private static string BuildHonoreeName(Honoree honoree)
    {
        return string.Join(" ", new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static string BuildServiceYears(Honoree honoree)
    {
        if (honoree.StartYear.HasValue && honoree.EndYear.HasValue) return $"{honoree.StartYear} - {honoree.EndYear}";
        if (honoree.StartYear.HasValue) return $"{honoree.StartYear} -";
        if (honoree.EndYear.HasValue) return $"- {honoree.EndYear}";
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

        return "Army.png";
    }

    private static string AssetPath(IWebHostEnvironment environment, string fileName)
    {
        return Path.Combine(environment.WebRootPath ?? AppContext.BaseDirectory, "assets", fileName);
    }
}
