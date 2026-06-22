using System.ComponentModel.DataAnnotations;

namespace PFOH.Api.Dtos;

public record AdminReviewItemDto(
    int ChangeRequestId,
    int ClaimId,
    int FlagGridId,
    string FlagGridName,
    int? HonoreeId,
    string HonoreeName,
    string? Rank,
    string? ServiceBranchName,
    string ClaimantEmail,
    string? ClaimantName,
    string RequestStatus,
    DateTime CreatedUtc,
    DateTime? SubmittedUtc,
    bool RequiresCardReprint,
    DateTime? CardPrintedUtc,
    string? ReviewNotes);

public record AdminPrintQueueItemDto(
    int ChangeRequestId,
    int ClaimId,
    int? HonoreeId,
    string HonoreeName,
    string FlagGridName,
    string? ServiceBranchName,
    string? PdfUrl,
    DateTime? ApprovedUtc,
    DateTime? CardPrintedUtc);

public class ApproveChangeRequest
{
    public bool RequiresCardReprint { get; set; } = true;

    [MaxLength(2000)]
    public string? ReviewNotes { get; set; }
}

public class RejectChangeRequest
{
    [Required, MaxLength(2000)]
    public string ReviewNotes { get; set; } = string.Empty;
}

public class BulkPrintRequest
{
    [Required]
    public List<int> ChangeRequestIds { get; set; } = new();
}
