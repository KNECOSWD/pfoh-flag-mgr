namespace PFOH.Api.Models;

public static class ReprintDuplicateReviewStatus
{
    public const string Flagged = "Flagged";
    public const string Dismissed = "Dismissed";
}

/// <summary>
/// A reprint-queue pair an administrator still has to review.
/// Flagged rows are inserts only. Dismiss keeps both queue items.
/// Approve records ResolvedUtc and does not add a third status.
/// </summary>
public class ReprintDuplicateReview
{
    public int Id { get; set; }

    public int HonoreeId { get; set; }

    public int DuplicateChangeRequestId { get; set; }

    public int KeptChangeRequestId { get; set; }

    public string Status { get; set; } = ReprintDuplicateReviewStatus.Flagged;

    public DateTime FlaggedUtc { get; set; }

    public DateTime? ResolvedUtc { get; set; }

    public string? ResolvedBy { get; set; }

    public Honoree? Honoree { get; set; }
}
