namespace PFOH.Api.Models;

public static class UserProfileLimits
{
    public const int OwnerObjectIdMaxLength = 128;
    public const int DisplayNameMaxLength = 100;
    public const int EmailMaxLength = 255;
}

/// <summary>
/// App-owned profile for a signed-in Entra user. Keyed by the token object id.
/// The sign-in email is copied from the token and is not editable here.
/// </summary>
public class UserProfile
{
    public int Id { get; set; }

    public string OwnerObjectId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
