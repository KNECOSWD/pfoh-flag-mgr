namespace PFOH.Api.Models;

public class ServiceBranch
{
    public int Id { get; set; }
    public int ServiceBranchCategoryId { get; set; }
    public string ServiceBranchName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LogoFileName { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public DateTime ModifiedDate { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime? DeletedDate { get; set; }

    public ServiceBranchCategory? ServiceBranchCategory { get; set; }
    public ICollection<Honoree> Honorees { get; set; } = new List<Honoree>();
}
