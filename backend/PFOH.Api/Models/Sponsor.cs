namespace PFOH.Api.Models;

public class Sponsor
{
    public int Id { get; set; }
    public int SponsorCategoryId { get; set; }

    public string Description { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string? Suffix { get; set; }
    public string? Nickname { get; set; }
    public string Salutation { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public string StreetAddress { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public string? LegacyKey { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public DateTime ModifiedDate { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime? DeletedDate { get; set; }

    public SponsorCategory? SponsorCategory { get; set; }
    public ICollection<Honoree> Honorees { get; set; } = new List<Honoree>();
}
