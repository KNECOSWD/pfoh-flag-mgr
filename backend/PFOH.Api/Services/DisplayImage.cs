using SkiaSharp;

namespace PFOH.Api.Services;

public static class DisplayImage
{
    public const int LogoEdgeSmall = 128;
    public const int LogoEdgeLarge = 256;
    public const int PhotoLongEdge = 800;
    public const int PhotoBudgetBytes = 110 * 1024;

    public static bool TryEncodeLogo(byte[] source, int edge, bool webp, out byte[] output)
    {
        output = [];
        try
        {
            return TryEncodeLogoCore(source, edge, webp, out output);
        }
        catch (Exception)
        {
            output = [];
            return false;
        }
    }

    private static bool TryEncodeLogoCore(byte[] source, int edge, bool webp, out byte[] output)
    {
        output = [];
        using var bitmap = SKBitmap.Decode(source);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        var longest = Math.Max(bitmap.Width, bitmap.Height);
        var target = Math.Min(Math.Max(edge, 1), longest);
        var scale = target / (float)longest;
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = bitmap.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
        if (resized is null)
        {
            return false;
        }

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(webp ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png, webp ? 82 : 100);
        if (data is null || data.Size <= 0)
        {
            return false;
        }

        output = data.ToArray();
        return true;
    }

    public static (byte[] Bytes, string ContentType) ForHonoreePhoto(byte[] source, string fallbackContentType)
    {
        if (source.Length == 0)
        {
            return (source, fallbackContentType);
        }

        try
        {
            return ResizeHonoreePhoto(source, fallbackContentType);
        }
        catch (Exception)
        {
            return (source, fallbackContentType);
        }
    }

    private static (byte[] Bytes, string ContentType) ResizeHonoreePhoto(byte[] source, string fallbackContentType)
    {
        using var stream = new MemoryStream(source, writable: false);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            return (source, fallbackContentType);
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            return (source, fallbackContentType);
        }

        using var oriented = Orient(decoded, codec.EncodedOrigin);
        var longest = Math.Max(oriented.Width, oriented.Height);
        if (longest <= PhotoLongEdge && source.Length <= PhotoBudgetBytes)
        {
            return (source, fallbackContentType);
        }

        var edge = Math.Min(PhotoLongEdge, longest);
        var scale = edge / (float)longest;
        var width = Math.Max(1, (int)Math.Round(oriented.Width * scale));
        var height = Math.Max(1, (int)Math.Round(oriented.Height * scale));
        using var resized = width == oriented.Width && height == oriented.Height
            ? oriented.Copy()
            : oriented.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
        if (resized is null)
        {
            return (source, fallbackContentType);
        }

        byte[]? best = null;
        foreach (var quality in new[] { 78, 68, 58, 48, 38 })
        {
            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data is null || data.Size <= 0)
            {
                continue;
            }

            best = data.ToArray();
            if (best.Length <= PhotoBudgetBytes)
            {
                break;
            }
        }

        return best is null ? (source, fallbackContentType) : (best, "image/jpeg");
    }

    private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var upright = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftTop;
        if (!upright && origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return bitmap.Copy();
        }

        var width = upright ? bitmap.Height : bitmap.Width;
        var height = upright ? bitmap.Width : bitmap.Height;
        var rotated = new SKBitmap(width, height);
        using var canvas = new SKCanvas(rotated);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(width, height);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(bitmap, 0, 0);
        return rotated;
    }
}
