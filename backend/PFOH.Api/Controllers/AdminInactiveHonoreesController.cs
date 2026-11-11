using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PFOH.Api.Dtos;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/inactive-honorees")]
[Authorize(Policy = "AdminOnly")]
[EnableRateLimiting("AuthenticatedGet")]
public class AdminInactiveHonoreesController(InactiveHonoreeAdminService honorees) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<InactiveHonoreeListDto>> List(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = InactiveHonoreeAdminService.DefaultPageSize,
        CancellationToken ct = default)
    {
        return Ok(await honorees.ListAsync(q, page, pageSize, ct));
    }

    [HttpGet("free-slots")]
    public async Task<ActionResult<InactiveHonoreeFreeSlotListDto>> FreeSlots(
        [FromQuery] string? q,
        CancellationToken ct)
    {
        return Ok(await honorees.ListFreeSlotsAsync(q, ct));
    }

    [HttpPost("reactivate")]
    public async Task<ActionResult<InactiveHonoreeReactivateResultDto>> Reactivate(
        [FromBody] InactiveHonoreeReactivateBody? body,
        CancellationToken ct)
    {
        var items = body?.Items;
        if (items is null || items.Count == 0)
        {
            return BadRequest(new { message = "Select at least one honoree." });
        }

        if (items.Count > InactiveHonoreeAdminService.MaxBatch)
        {
            return BadRequest(new { message = $"Restore up to {InactiveHonoreeAdminService.MaxBatch} honorees at a time." });
        }

        var result = await honorees.ReactivateAsync(items, AdminActor.From(User), ct);
        if (result.Results.Count == 1)
        {
            var only = result.Results[0];
            if (only.Outcome == "not-found")
            {
                return NotFound(new { message = only.Message, results = result.Results });
            }

            if (only.Outcome != "reactivated")
            {
                return Conflict(new { message = only.Message, results = result.Results });
            }
        }

        return Ok(result);
    }
}
