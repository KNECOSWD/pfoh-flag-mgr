using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/lookups")]
[Authorize]
public class LookupsController(PfohDbContext db) : ControllerBase
{
    [HttpGet("service-branches")]
    public async Task<ActionResult<IReadOnlyList<ServiceBranchDto>>> GetServiceBranches(CancellationToken ct)
    {
        var items = await db.ServiceBranches
            .AsNoTracking()
            .Where(b => b.DeletedDate == null)
            .OrderBy(b => b.ServiceBranchName)
            .Select(b => new ServiceBranchDto(
                b.Id,
                b.ServiceBranchCategoryId,
                b.ServiceBranchName,
                b.Description))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("service-branch-categories")]
    public async Task<ActionResult<IReadOnlyList<ServiceBranchCategoryDto>>> GetServiceBranchCategories(CancellationToken ct)
    {
        var items = await db.ServiceBranchCategories
            .AsNoTracking()
            .Where(c => c.DeletedDate == null)
            .OrderBy(c => c.ServiceBranchCategoryName)
            .Select(c => new ServiceBranchCategoryDto(
                c.Id,
                c.ServiceBranchCategoryName,
                c.Description))
            .ToListAsync(ct);

        return Ok(items);
    }
}
