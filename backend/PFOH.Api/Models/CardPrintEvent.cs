namespace PFOH.Api.Models;

public static class CardPrintEventLimits
{
    public const int PrintedByMaxLength = 255;
    public const int CommentMaxLength = 500;
}

/// <summary>
/// One row for each time an administrator marks a print batch as printed.
/// The batch id is the same value stored on each card's CardPrintBatchId.
/// </summary>
public class CardPrintEvent
{
    public int Id { get; set; }
    public Guid CardPrintBatchId { get; set; }
    public string PrintedBy { get; set; } = string.Empty;
    public DateTime PrintedUtc { get; set; }
    public int CardCount { get; set; }
    public string? Comment { get; set; }
}
