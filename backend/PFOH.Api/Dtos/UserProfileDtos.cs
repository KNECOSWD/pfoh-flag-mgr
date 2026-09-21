namespace PFOH.Api.Dtos;

public record UserProfileDto(
    string OwnerObjectId,
    string DisplayName,
    string Email,
    bool HasSavedDisplayName);

public record UpdateUserProfileRequest
{
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional. When present, it must match the signed-in user. Email is not accepted.
    /// </summary>
    public string? OwnerObjectId { get; set; }
}
