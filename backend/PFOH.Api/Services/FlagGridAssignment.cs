using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public class FlagGridAssignment(PfohDbContext db)
{
    public static readonly string[] SpotHoldingStatuses = ["Claimed", "Submitted", "Approved"];

    public const string NoSpotNotice =
        "This honoree was approved and added, but no flag spot is available. Free a spot and assign the honoree manually in the Flag Map.";

    public async Task<FlagGrid?> NextAvailableAsync(CancellationToken ct)
    {
        return await db.FlagGrids
            .Where(flagGrid =>
                !flagGrid.Reserved &&
                flagGrid.DeletedDate == null &&
                flagGrid.HonoreeId == null &&
                !db.Honorees.Any(h =>
                    h.IsActive &&
                    h.DeletedDate == null &&
                    h.FlagGridId == flagGrid.Id) &&
                !db.FlagClaims.Any(claim =>
                    claim.FlagGridId == flagGrid.Id &&
                    SpotHoldingStatuses.Contains(claim.ClaimStatus)))
            .OrderBy(flagGrid => flagGrid.FlagGridName)
            .FirstOrDefaultAsync(ct);
    }
}
