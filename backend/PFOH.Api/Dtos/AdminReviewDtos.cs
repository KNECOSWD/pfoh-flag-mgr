using System.ComponentModel.DataAnnotations;
using PFOH.Api.Models;

namespace PFOH.Api.Dtos;

public record AdminReviewItemDto(
    int ChangeRequestId,
    int ClaimId,
    int? FlagGridId,
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
    string? ReviewNotes,
    string? PhotoFileName,
    string? Description,
    string? Awards,
    string? ConflictsServed,
    string? ClaimantPhone = null,
    string? ClaimantOrganization = null,
    string SubmissionKind = "New",
    IReadOnlyList<ReviewFieldChangeDto>? FieldChanges = null,
    bool? FlagSpotAssigned = null,
    string? FlagSpotNotice = null,
    string? RequesterNote = null,
    DateTime? LinkUsedUtc = null);

public record ReviewFieldChangeDto(string Label, string Previous, string Proposed);

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

public class MarkPrintedRequest
{
    [Required]
    public List<int> ChangeRequestIds { get; set; } = new();

    [MaxLength(CardPrintEventLimits.CommentMaxLength)]
    public string? Comment { get; set; }
}

public sealed record CardPrintEventDto(
    Guid BatchId,
    DateTime PrintedUtc,
    string PrintedBy,
    int CardCount,
    string? Comment);

public sealed record PrintBatchCardDto(int ChangeRequestId, int HonoreeId, string HonoreeName);

public sealed record PrintBatchPlanDto(IReadOnlyList<PrintBatchCardDto> Cards, IReadOnlyList<int> NotInQueue);
