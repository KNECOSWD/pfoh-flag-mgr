using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FlagsController(PfohDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<FlagDto>>> GetMyFlags(CancellationToken ct)
    {
        var ownerId = GetOwnerObjectId();
        var flags = await db.Flags
            .Where(f => f.OwnerObjectId == ownerId)
            .OrderBy(f => f.HonoreeName)
            .Select(f => new FlagDto(
                f.Id,
                f.HonoreeName,
                f.ServiceBranch,
                f.RankOrTitle,
                f.FlagNumber,
                f.GridLocation,
                f.TributeText,
                f.Status,
                f.CreatedUtc,
                f.UpdatedUtc))
            .ToListAsync(ct);

        return Ok(flags);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<FlagDto>> GetMyFlag(int id, CancellationToken ct)
    {
        var ownerId = GetOwnerObjectId();
        var flag = await db.Flags.FirstOrDefaultAsync(f => f.Id == id && f.OwnerObjectId == ownerId, ct);
        return flag is null ? NotFound() : Ok(flag.ToDto());
    }

    [HttpPost]
    public async Task<ActionResult<FlagDto>> CreateFlag([FromBody] UpsertFlagRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var flag = new FlagRecord
        {
            OwnerObjectId = GetOwnerObjectId(),
            HonoreeName = request.HonoreeName.Trim(),
            ServiceBranch = request.ServiceBranch?.Trim(),
            RankOrTitle = request.RankOrTitle?.Trim(),
            FlagNumber = request.FlagNumber?.Trim(),
            GridLocation = request.GridLocation?.Trim(),
            TributeText = request.TributeText?.Trim(),
            Status = string.IsNullOrWhiteSpace(request.Status) ? "Draft" : request.Status.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        db.Flags.Add(flag);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetMyFlag), new { id = flag.Id }, flag.ToDto());
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<FlagDto>> UpdateFlag(int id, [FromBody] UpsertFlagRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var ownerId = GetOwnerObjectId();
        var flag = await db.Flags.FirstOrDefaultAsync(f => f.Id == id && f.OwnerObjectId == ownerId, ct);
        if (flag is null) return NotFound();

        flag.HonoreeName = request.HonoreeName.Trim();
        flag.ServiceBranch = request.ServiceBranch?.Trim();
        flag.RankOrTitle = request.RankOrTitle?.Trim();
        flag.FlagNumber = request.FlagNumber?.Trim();
        flag.GridLocation = request.GridLocation?.Trim();
        flag.TributeText = request.TributeText?.Trim();
        flag.Status = string.IsNullOrWhiteSpace(request.Status) ? "Draft" : request.Status.Trim();
        flag.UpdatedUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(flag.ToDto());
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteFlag(int id, CancellationToken ct)
    {
        var ownerId = GetOwnerObjectId();
        var flag = await db.Flags.FirstOrDefaultAsync(f => f.Id == id && f.OwnerObjectId == ownerId, ct);
        if (flag is null) return NotFound();

        db.Flags.Remove(flag);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private string GetOwnerObjectId()
    {
        var oid = User.FindFirstValue("oid")
            ?? User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(oid))
        {
            throw new InvalidOperationException("Authenticated user is missing an object identifier claim.");
        }

        return oid;
    }
}
