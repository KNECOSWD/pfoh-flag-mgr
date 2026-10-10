namespace PFOH.Api.Models;

public static class UserProfileLimits
{
    public const int OwnerObjectIdMaxLength = 128;
    public const int DisplayNameMaxLength = 100;
    public const int EmailMaxLength = 255;
    public const int PhoneMaxLength = 16;
    public const int OrganizationMaxLength = 200;
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

    /// <summary>US phone number stored as +1 and 10 digits. Required for new and updated profiles. Empty only on rows created before the column existed.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Optional organization. There is no city or state column on this table.</summary>
    public string? Organization { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
