namespace PFOH.Api.Models;

/// <summary>
/// One remove or restore of an honoree on a flag spot. Before and after
/// snapshots include the spot, the honoree, open claims, pending change
/// requests, and unprinted reprint items.
/// </summary>
public class FlagGridRemovalAudit
{
    public int Id { get; set; }

    public int HonoreeId { get; set; }

    /// <summary>
    /// The flag spot this remove or restore touched. Null when an administrator
    /// reactivated an honoree who had no flag on record.
    /// </summary>
    public int? FlagGridId { get; set; }

    public int? RemovalQueueItemId { get; set; }

    /// <summary>Remove or Restore.</summary>
    public string Action { get; set; } = string.Empty;

    public string ActorEmail { get; set; } = string.Empty;

    public string? ActorObjectId { get; set; }

    public DateTime OccurredUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string BeforeState { get; set; } = "{}";

    public string AfterState { get; set; } = "{}";

    public string? PdfWarning { get; set; }
}
