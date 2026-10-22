namespace PFOH.Api.Models;

public static class UserProfileLimits
{
    public const int OwnerObjectIdMaxLength = 128;
    public const int DisplayNameMaxLength = 100;
    public const int EmailMaxLength = 255;
    public const int PhoneMaxLength = 16;
    public const int OrganizationMaxLength = 200;
    public const int FieldSourceMaxLength = 16;
}

/// <summary>
/// Where a profile field's current value came from.
/// <see cref="Claim"/> is copied from the Entra token and refreshed when that claim changes.
/// <see cref="Profile"/> was saved from the profile form and is left alone.
/// A null source on a row that already existed is treated as <see cref="Profile"/>:
/// those rows were saved before this flag existed, and the token must not overwrite them.
/// </summary>
public static class ProfileFieldSource
{
    public const string Claim = "claim";
    public const string Profile = "profile";
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

    /// <summary><see cref="ProfileFieldSource.Claim"/> or <see cref="ProfileFieldSource.Profile"/>. Null on rows saved before the flag existed.</summary>
    public string? DisplayNameSource { get; set; }

    /// <summary><see cref="ProfileFieldSource.Claim"/> or <see cref="ProfileFieldSource.Profile"/>. Null on rows saved before the flag existed.</summary>
    public string? PhoneSource { get; set; }

    /// <summary><see cref="ProfileFieldSource.Claim"/> or <see cref="ProfileFieldSource.Profile"/>. Null on rows saved before the flag existed.</summary>
    public string? OrganizationSource { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
