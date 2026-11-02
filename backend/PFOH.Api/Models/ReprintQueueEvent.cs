namespace PFOH.Api.Models;

/// <summary>
/// One add, removal, duplicate removal, or print of a reprint-queue item.
/// The row records who did it, when, and why. It does not change the queue item.
/// </summary>
public class ReprintQueueEvent
{
    public int Id { get; set; }

    public int ChangeRequestId { get; set; }

    public int? HonoreeId { get; set; }

    /// <summary>Add, Remove, DedupRemove, or Printed.</summary>
    public string Action { get; set; } = string.Empty;

    public string ActorEmail { get; set; } = string.Empty;

    public string? ActorObjectId { get; set; }

    public DateTime Utc { get; set; }

    public string? Reason { get; set; }

    /// <summary>row, bulk, dedup, queue, print, or flag-removal.</summary>
    public string Source { get; set; } = string.Empty;

    public int BatchSize { get; set; }
}
