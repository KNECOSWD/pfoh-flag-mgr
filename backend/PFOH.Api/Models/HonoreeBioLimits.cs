namespace PFOH.Api.Models;

/// <summary>
/// Character cap for an honoree bio (the Description field).
/// Measured for the printed card in <c>HonoreeReportPdfGenerator</c>:
/// DejaVu Sans 12pt in a 214 by 345 point box. Twenty-four lines fit
/// (line height 13.969pt, 335.25pt) and a twenty-fifth line (349.22pt) does not.
/// Text longer than 650 characters shrinks the font, so this cap stays below that.
/// Typical prose fits 845 characters at 12pt. Words of nine W's, the widest
/// Latin letter that still wraps inside the box, fit 248 characters and clip at 249.
/// 248 is rounded down to 200. There is no database length change.
/// </summary>
public static class HonoreeBioLimits
{
    public const int MaxLength = 200;

    public const string TooLongMessage = "Bio must be 200 characters or fewer.";
}
