using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/honorees")]
[Authorize]
public class HonoreesController(PfohDbContext db) : ControllerBase
{
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<HonoreeSearchResultDto>>> Search(
        [FromQuery] string? q,
        [FromQuery] int take = 25,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 50);

        var query = db.HonoreeSearchResults
            .AsNoTracking()
            .Where(h => h.IsActive);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var pattern = $"%{term}%";

            query = query.Where(h =>
                EF.Functions.Like(h.FullName, pattern) ||
                EF.Functions.Like(h.FirstName, pattern) ||
                EF.Functions.Like(h.LastName, pattern) ||
                (h.Nickname != null && EF.Functions.Like(h.Nickname, pattern)) ||
                EF.Functions.Like(h.Rank, pattern) ||
                (h.ServiceBranchName != null && EF.Functions.Like(h.ServiceBranchName, pattern)) ||
                (h.FlagGrid != null && EF.Functions.Like(h.FlagGrid, pattern)) ||
                (h.SponsorName != null && EF.Functions.Like(h.SponsorName, pattern)));
        }

        var results = await query
            .OrderBy(h => h.LastName)
            .ThenBy(h => h.FirstName)
            .Take(take)
            .Select(h => new HonoreeSearchResultDto(
                h.Id,
                h.FullName,
                h.FirstName,
                h.MiddleName,
                h.LastName,
                h.Suffix,
                h.Nickname,
                h.Rank,
                h.KIA,
                h.ServiceBranchName,
                h.FlagGrid,
                h.SponsorName,
                h.ImageUrl,
                h.PDFUrl,
                h.IsActive))
            .ToListAsync(ct);

        return Ok(results);
    }
}
