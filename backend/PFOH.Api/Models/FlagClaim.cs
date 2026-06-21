namespace PFOH.Api.Models;

public class FlagClaim
{
    public int Id { get; set; }

    public int FlagGridId { get; set; }
    public int? HonoreeId { get; set; }

    // Object ID from the External ID / Entra token.
    public string ExternalUserObjectId { get; set; } = string.Empty;
    public string ExternalUserEmail { get; set; } = string.Empty;
    public string? ExternalUserName { get; set; }

    // Claimed, Submitted, Approved, Rejected, Cancelled.
    public string ClaimStatus { get; set; } = "Claimed";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedUtc { get; set; }
    public DateTime? ApprovedUtc { get; set; }
    public DateTime? RejectedUtc { get; set; }

    public string? ApprovedBy { get; set; }
    public string? RejectedBy { get; set; }
    public string? AdminNotes { get; set; }

    public FlagGrid? FlagGrid { get; set; }
    public Honoree? Honoree { get; set; }
    public ICollection<HonoreeChangeRequest> HonoreeChangeRequests { get; set; } = new List<HonoreeChangeRequest>();
}
