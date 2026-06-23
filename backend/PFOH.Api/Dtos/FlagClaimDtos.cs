using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace PFOH.Api.Dtos;

public record FlagClaimDto(
    int Id,
    int FlagGridId,
    string FlagGridName,
    int? HonoreeId,
    string HonoreeName,
    string? HonoreeImageUrl,
    string ClaimStatus,
    string ExternalUserEmail,
    string? ExternalUserName,
    DateTime CreatedUtc,
    DateTime? SubmittedUtc,
    HonoreeChangeRequestDto? LatestChangeRequest,
    bool HasOtherClaimants = false,
    int OtherClaimantCount = 0,
    string? ClaimNotice = null);

public record HonoreeChangeRequestDto(
    int Id,
    int FlagClaimId,
    int FlagGridId,
    int? HonoreeId,
    string FirstName,
    string? MiddleName,
    string LastName,
    string? Suffix,
    string? Nickname,
    string? Rank,
    int? ServiceBranchId,
    int? ServiceBranchCategoryId,
    int? StartYear,
    int? EndYear,
    string? DatesUserEntry,
    string? ConflictsServed,
    string? Awards,
    string? Description,
    bool Kia,
    string? PhotoFileName,
    string? SubmitterPhoneNumber,
    string? SubmitterEmailAddress,
    string RequestStatus,
    DateTime CreatedUtc,
    DateTime? SubmittedUtc);

public class SaveHonoreeChangeRequest
{
    [Required, MaxLength(200)]
    public string FirstName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? MiddleName { get; set; }

    [Required, MaxLength(200)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Suffix { get; set; }

    [MaxLength(200)]
    public string? Nickname { get; set; }

    [MaxLength(200)]
    public string? Rank { get; set; }

    [Required]
    public int? ServiceBranchId { get; set; }

    [Required]
    public int? ServiceBranchCategoryId { get; set; }

    public int? StartYear { get; set; }
    public int? EndYear { get; set; }

    [MaxLength(500)]
    public string? DatesUserEntry { get; set; }

    public string? ConflictsServed { get; set; }
    public string? Awards { get; set; }
    [Required]
    public string? Description { get; set; }

    public bool Kia { get; set; }

    [Required, MaxLength(50)]
    public string? SubmitterPhoneNumber { get; set; }

    [Required, MaxLength(255), EmailAddress]
    public string? SubmitterEmailAddress { get; set; }

    public IFormFile? Photo { get; set; }
}

public class NominateHonoreeRequest : SaveHonoreeChangeRequest
{
}
