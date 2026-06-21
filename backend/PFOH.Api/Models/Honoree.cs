namespace PFOH.Api.Models;

public class Honoree
{
    public int Id { get; set; }

    public int? SponsorId { get; set; }
    public int? FlagGridId { get; set; }

    public bool KIA { get; set; }
    public string Description { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string? Suffix { get; set; }
    public string? Nickname { get; set; }
    public string Salutation { get; set; } = string.Empty;

    public string Rank { get; set; } = string.Empty;
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
    public string DatesUserEntry { get; set; } = string.Empty;
    public string NameUserEntry { get; set; } = string.Empty;
    public string ConflictsServed { get; set; } = string.Empty;
    public string Awards { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;

    public int? ServiceBranchCategoryId { get; set; }
    public int? ServiceBranchId { get; set; }

    public string? FlagLocation { get; set; }
    public bool IsActive { get; set; }

    public string PhotoFileName { get; set; } = string.Empty;
    public int? LegacyKey { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public DateTime ModifiedDate { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime? DeletedDate { get; set; }

    public Sponsor? Sponsor { get; set; }
    public FlagGrid? FlagGrid { get; set; }
    public ServiceBranch? ServiceBranch { get; set; }
    public ServiceBranchCategory? ServiceBranchCategory { get; set; }
}
