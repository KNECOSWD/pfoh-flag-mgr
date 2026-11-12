namespace PFOH.Api.Dtos;

public sealed record InactiveHonoreeDto(
    int HonoreeId,
    string Name,
    string? Rank,
    string? Branch,
    int? FormerFlagGridId,
    string? FormerFlagGridName,
    DateTime? DeactivatedUtc,
    string DeactivatedBy,
    string Reason,
    bool CanReactivate,
    string? BlockReason);

public sealed record InactiveHonoreeListDto(
    IReadOnlyList<InactiveHonoreeDto> Items,
    int Page,
    int PageSize,
    int Total);

public sealed class InactiveHonoreeReactivateBody
{
    public List<InactiveHonoreeReactivateItem>? Items { get; set; }
}

public sealed class InactiveHonoreeReactivateItem
{
    public int HonoreeId { get; set; }
}

public sealed record InactiveHonoreeReactivateItemResultDto(
    int HonoreeId,
    string Name,
    string Outcome,
    string Code,
    string Message,
    int? ChangeRequestId);

public sealed record InactiveHonoreeReactivateResultDto(
    IReadOnlyList<InactiveHonoreeReactivateItemResultDto> Results);
