namespace PFOH.Api.Models;

public class FlagRecord
{
    public int Id { get; set; }
    public string OwnerObjectId { get; set; } = string.Empty;
    public string HonoreeName { get; set; } = string.Empty;
    public string? ServiceBranch { get; set; }
    public string? RankOrTitle { get; set; }
    public string? FlagNumber { get; set; }
    public string? GridLocation { get; set; }
    public string? TributeText { get; set; }
    public string Status { get; set; } = "Draft";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
