namespace PFOH.Api.Dtos;

public sealed record ReactivationSummaryDto(
    string Name,
    string? Rank,
    string? ServiceBranch,
    string? Years,
    string? ConflictsServed,
    string? Awards,
    string? Description,
    bool Kia);

public sealed record ReactivationLinkStateDto(
    string Status,
    string Message,
    ReactivationSummaryDto? Honoree);

public sealed class ReactivationSubmitBody
{
    public string? Note { get; set; }
}

public sealed class ReactivationGenerateBody
{
    public List<int>? HonoreeIds { get; set; }
}

public sealed record ReactivationEligibleDto(
    int HonoreeId,
    string Name,
    string RequesterEmail,
    DateTime? ActiveLinkExpiresUtc);

public sealed record ReactivationLinkRowDto(
    string HonoreeId,
    string Name,
    string RequesterEmail,
    string Link,
    string Expiry,
    string EligibilityReason);

public sealed record ReactivationGenerateResultDto(
    string Csv,
    IReadOnlyList<ReactivationLinkRowDto> Rows);

public sealed record ReactivationRevokeResultDto(bool Revoked);
