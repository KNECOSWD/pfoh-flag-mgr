namespace PFOH.Api.Models;

/// <summary>
/// One flag card that left a spot. The name and spot are copied onto the row
/// so the list still reads after a merge or deactivation. Done rows are kept.
/// </summary>
public class FlagRemovalQueueItem
{
    public int Id { get; set; }

    public int? HonoreeId { get; set; }

    public string HonoreeName { get; set; } = string.Empty;

    /// <summary>The spot to pull the old card from.</summary>
    public int FlagGridId { get; set; }

    public string FlagGridName { get; set; } = string.Empty;

    public int? NewFlagGridId { get; set; }

    public string? NewFlagGridName { get; set; }

    /// <summary>Manual, Move, Merge, or Other.</summary>
    public string Source { get; set; } = string.Empty;

    public string? SourceDetail { get; set; }

    public int? MergeAuditId { get; set; }

    public HonoreeMergeAudit? MergeAudit { get; set; }

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? DoneUtc { get; set; }

    public string? DoneBy { get; set; }

    public DateTime? CanceledUtc { get; set; }

    public string? CanceledBy { get; set; }

    public string? CancelReason { get; set; }
}
