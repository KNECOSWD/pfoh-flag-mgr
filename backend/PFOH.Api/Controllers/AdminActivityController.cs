using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/activity")]
[Authorize(Policy = "AdminOnly")]
[EnableRateLimiting("AuthenticatedGet")]
public class AdminActivityController(PfohDbContext db, ActivityPageViews pageViews) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminActivityResponse>> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var activity = await AdminActivityQuery.ReadAsync(db, DateTime.UtcNow, ct);
        return Ok(activity with { PageViews = await pageViews.ReadAsync(ct) });
    }
}
