namespace PFOH.Api.Models;

public class SponsorCategory
{
    public int Id { get; set; }
    public string SponsorCategoryName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public DateTime ModifiedDate { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime? DeletedDate { get; set; }

    public ICollection<Sponsor> Sponsors { get; set; } = new List<Sponsor>();
}
