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
    string SlotStatus,
    string? SlotDetail,
    bool CanReactivate);

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

    public int? FlagGridId { get; set; }
}

public sealed record InactiveHonoreeReactivateItemResultDto(
    int HonoreeId,
    string Name,
    string Outcome,
    string Code,
    string Message,
    int? FlagGridId,
    string? FlagGridName,
    bool ReprintQueued,
    bool RemovalCanceled,
    string? PdfWarning);

public sealed record InactiveHonoreeReactivateResultDto(
    IReadOnlyList<InactiveHonoreeReactivateItemResultDto> Results);

public sealed record InactiveHonoreeFreeSlotDto(int Id, string FlagGridName);

public sealed record InactiveHonoreeFreeSlotListDto(
    IReadOnlyList<InactiveHonoreeFreeSlotDto> Items,
    bool Truncated);
