namespace PFOH.Api.Models;

/// <summary>
/// Character cap for an honoree bio (the Description field).
/// Measured for the printed card in <c>HonoreeReportPdfGenerator</c>:
/// DejaVu Sans 12pt in a 214 by 345 point box. Twenty-four lines fit
/// (line height 13.969pt, 335.25pt). Text longer than 650 characters shrinks.
/// All-caps prose with long words and names fits 625 characters in 24 lines.
/// 600 is the largest multiple of 50 at or under both that fit and 650.
/// There is no database length change.
/// </summary>
public static class HonoreeBioLimits
{
    public const int MaxLength = 600;

    public const string TooLongMessage = "Bio must be 600 characters or fewer.";
}
