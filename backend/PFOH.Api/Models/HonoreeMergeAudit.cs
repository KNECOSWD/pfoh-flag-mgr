namespace PFOH.Api.Models;

/// <summary>
/// One merge. The before-state is enough to put the survivor, the absorbed
/// records, and the rows the merge moved back the way they were.
/// </summary>
public class HonoreeMergeAudit
{
    public int Id { get; set; }

    public int SurvivorId { get; set; }

    public string AbsorbedHonoreeIds { get; set; } = "[]";

    public string BeforeState { get; set; } = "{}";

    public string AfterSurvivorState { get; set; } = "{}";

    public string CreatedHonoreeClaimIds { get; set; } = "[]";

    public string MergedBy { get; set; } = string.Empty;

    public DateTime MergedUtc { get; set; }

    public DateTime SurvivorModifiedUtc { get; set; }

    public string? UndoneBy { get; set; }

    public DateTime? UndoneUtc { get; set; }
}
