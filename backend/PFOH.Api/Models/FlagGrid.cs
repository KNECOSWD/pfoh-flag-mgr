namespace PFOH.Api.Models;

public class FlagGrid
{
    public int Id { get; set; }
    public int? HonoreeId { get; set; }
    public string FlagGridName { get; set; } = string.Empty;
    public bool Reserved { get; set; }
    public string Notes { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public DateTime ModifiedDate { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime? DeletedDate { get; set; }

    public Honoree? Honoree { get; set; }
    public ICollection<Honoree> AssignedHonorees { get; set; } = new List<Honoree>();
    public ICollection<FlagClaim> FlagClaims { get; set; } = new List<FlagClaim>();
}
