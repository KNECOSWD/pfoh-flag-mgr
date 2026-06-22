namespace PFOH.Api.Dtos;

public record HonoreeSearchResultDto(
    int Id,
    string FullName,
    string FirstName,
    string? MiddleName,
    string LastName,
    string? Suffix,
    string? Nickname,
    string Rank,
    bool Kia,
    string? ServiceBranchName,
    string? FlagGrid,
    string? SponsorName,
    string? ImageUrl,
    string? PdfUrl,
    bool IsActive);
