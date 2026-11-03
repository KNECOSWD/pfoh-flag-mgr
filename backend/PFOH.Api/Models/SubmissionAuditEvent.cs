namespace PFOH.Api.Models;

public class SubmissionAuditEvent
{
    public const string Superseded = "Superseded";
    public const string Restored = "Restored";

    public int Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public int ChangeRequestId { get; set; }

    public int? KeeperChangeRequestId { get; set; }

    public string ActorEmail { get; set; } = string.Empty;

    public string? ActorObjectId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public string? Note { get; set; }
}
