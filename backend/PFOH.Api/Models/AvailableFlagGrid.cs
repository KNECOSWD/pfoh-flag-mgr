namespace PFOH.Api.Models;

// Read-only EF model mapped to dbo.vw_AvailableFlagGrids.
public class AvailableFlagGrid
{
    public int Id { get; set; }
    public string FlagGridName { get; set; } = string.Empty;
    public bool Reserved { get; set; }
}
