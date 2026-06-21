using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/flag-grids")]
[Authorize]
public class FlagGridsController(PfohDbContext db) : ControllerBase
{
    [HttpGet("available")]
    public async Task<ActionResult<IReadOnlyList<AvailableFlagGridDto>>> GetAvailable(CancellationToken ct)
    {
        var activeClaimStatuses = new[] { "Claimed", "Submitted", "Approved" };

        var claimedGridIds = db.FlagClaims
            .Where(c => activeClaimStatuses.Contains(c.ClaimStatus))
            .Select(c => c.FlagGridId);

        var flags = await db.AvailableFlagGrids
            .Where(f => !claimedGridIds.Contains(f.Id))
            .OrderBy(f => f.FlagGridName)
            .Select(f => new AvailableFlagGridDto(f.Id, f.FlagGridName, f.Reserved))
            .ToListAsync(ct);

        return Ok(flags);
    }
}
