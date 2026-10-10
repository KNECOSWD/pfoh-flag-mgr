namespace PFOH.Api.Dtos;

public record UserProfileDto(
    string OwnerObjectId,
    string DisplayName,
    string Email,
    bool HasSavedDisplayName,
    string Phone,
    string Organization,
    bool PhoneNeedsCorrection = false);

public record UpdateUserProfileRequest
{
    /// <summary>Account name. Stored as <see cref="UserProfileDto.DisplayName"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Required US phone number. Stored as +1 and 10 digits, such as +12145551234.</summary>
    public string? Phone { get; set; }

    /// <summary>Optional organization. Blank clears a previously saved value.</summary>
    public string? Organization { get; set; }

    /// <summary>
    /// Optional. When present, it must match the signed-in user. Email is not accepted.
    /// </summary>
    public string? OwnerObjectId { get; set; }
}
