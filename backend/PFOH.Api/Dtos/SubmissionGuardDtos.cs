namespace PFOH.Api.Dtos;

public sealed record SubmissionMatchDto(
    string Kind,
    int? HonoreeId,
    int? ChangeRequestId,
    string Name,
    string? Rank,
    string? Branch,
    string? FlagLocation,
    string Likelihood,
    string Reason,
    bool CanUpdateDirectly,
    string? MatchToken);

public sealed record DuplicateCheckResponse(
    bool Enabled,
    IReadOnlyList<SubmissionMatchDto> Matches);

public sealed class DuplicateCheckRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string? Suffix { get; set; }
    public string? Nickname { get; set; }
    public string? Rank { get; set; }
    public int? ServiceBranchId { get; set; }
    public string? SubmitterEmailAddress { get; set; }
    public int? ExcludeHonoreeId { get; set; }
    public int? ExcludeChangeRequestId { get; set; }
}

public sealed record SubmissionReceiptDto(
    string Reference,
    string Status,
    string Name,
    string? Rank,
    string? Branch,
    string? FlagLocation,
    string? Tribute,
    string NextSteps);

public sealed record SupersededSubmissionDto(
    int ChangeRequestId,
    string HonoreeName,
    string? Rank,
    string? Branch,
    DateTime? SubmittedUtc,
    string SubmittedCentral,
    string? SubmitterEmail,
    int? KeeperChangeRequestId,
    string? KeeperName);

public sealed record SupersededSubmissionPage(
    IReadOnlyList<SupersededSubmissionDto> Items,
    int Page,
    int PageSize,
    int Total);
