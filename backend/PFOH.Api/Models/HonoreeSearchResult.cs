namespace PFOH.Api.Models;

// Read-only EF model mapped to dbo.vwHonorees.
public class HonoreeSearchResult
{
    public int Id { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;

    public string? FlagLocation { get; set; }
    public string? Nickname { get; set; }
    public string Awards { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    public string? ImageUrl { get; set; }
    public string? PDFUrl { get; set; }

    public string Rank { get; set; } = string.Empty;
    public bool KIA { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Suffix { get; set; }

    public int? ServiceBranchId { get; set; }
    public string? ServiceBranchName { get; set; }

    public string? FlagGrid { get; set; }

    public int? SponsorId { get; set; }
    public string? SponsorName { get; set; }
}
