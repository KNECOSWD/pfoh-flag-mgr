namespace PFOH.Api.Models;

/// <summary>
/// One person who claims an honoree. An honoree can have many of these rows.
/// dbo.FlagClaims stays the submission and reprint workflow. There is no single
/// claimant column on dbo.Honorees to remove.
/// </summary>
public class HonoreeClaim
{
    public int Id { get; set; }

    public int HonoreeId { get; set; }

    // Entra object id. Same value FlagClaim stores as ExternalUserObjectId.
    public string UserId { get; set; } = string.Empty;

    public DateTime ClaimedUtc { get; set; }

    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string ClaimStatus { get; set; } = "Claimed";

    // The FlagClaim this row was copied from. Not a foreign key, so a workflow
    // row can be removed without deleting the claimant list.
    public int? SourceFlagClaimId { get; set; }

    public Honoree? Honoree { get; set; }
}
