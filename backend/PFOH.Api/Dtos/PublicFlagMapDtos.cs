namespace PFOH.Api.Dtos;

public sealed class KioskExitRequest
{
    public string? Pin { get; set; }
}

public sealed class SubmitterLookupRequest
{
    public string? First { get; set; }
    public string? Last { get; set; }
}

public sealed class SubmitterSuggestionRequest
{
    public string? Query { get; set; }
}

public sealed class SubmitterTokenRequest
{
    public string? Token { get; set; }
}

public record PublicFlagSectionDto(
    string Section,
    int FlagCount,
    int OccupiedCount);

public record PublicFlagSeatDto(
    int FlagGridId,
    string FlagGridName,
    string Section,
    int? ColumnNumber,
    string Status,
    int? HonoreeId,
    string? DisplayName,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? Suffix,
    string? Nickname,
    string? Rank,
    string? ServiceBranchName,
    string? ServiceBranchCategoryName,
    string? LogoUrl,
    int? StartYear,
    int? EndYear,
    string? DatesUserEntry,
    bool? Kia,
    string? Description,
    string? ConflictsServed,
    string? Awards,
    string? PhotoUrl);

public record PublicFlagSearchDto(
    int HonoreeId,
    int FlagGridId,
    string FlagGridName,
    string Section,
    int? ColumnNumber,
    string DisplayName,
    string? Rank,
    string? ServiceBranchName,
    string? ServiceBranchCategoryName,
    string? LogoUrl);
