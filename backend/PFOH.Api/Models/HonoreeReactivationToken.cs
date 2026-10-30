namespace PFOH.Api.Models;

/// <summary>
/// One-time reactivation link for an inactive honoree. The raw token is never stored.
/// TokenHash is the SHA-256 of the token bytes.
/// </summary>
public class HonoreeReactivationToken
{
    public int Id { get; set; }

    public int HonoreeId { get; set; }

    public byte[] TokenHash { get; set; } = [];

    public string RequesterEmail { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime ExpiresUtc { get; set; }

    public DateTime? UsedUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Honoree? Honoree { get; set; }
}
