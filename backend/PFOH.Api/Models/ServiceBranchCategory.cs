namespace PFOH.Api.Models;

public class ServiceBranchCategory
{
    public int Id { get; set; }
    public string ServiceBranchCategoryName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public DateTime ModifiedDate { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime? DeletedDate { get; set; }

    public ICollection<ServiceBranch> ServiceBranches { get; set; } = new List<ServiceBranch>();
    public ICollection<Honoree> Honorees { get; set; } = new List<Honoree>();
}
