using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/reactivation")]
[Authorize(Policy = "AdminOnly")]
[EnableRateLimiting("AuthenticatedGet")]
public class AdminReactivationController(
    ReactivationLinkService links,
    InactiveHonoreeAdminService inactive,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("inactive")]
    public async Task<ActionResult<InactiveHonoreeListDto>> Inactive(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = InactiveHonoreeAdminService.DefaultPageSize,
        CancellationToken ct = default)
    {
        return Ok(await inactive.ListUnflaggedAsync(q, page, pageSize, ct));
    }

    [HttpPost("review-queue")]
    public async Task<ActionResult<InactiveHonoreeReactivateResultDto>> AddToReviewQueue(
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
            return BadRequest(new { message = $"Add up to {InactiveHonoreeAdminService.MaxBatch} honorees at a time." });
        }

        var result = await inactive.AddToReviewQueueAsync(items, AdminActor.From(User), ct);
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

    [HttpGet("eligible")]
    public async Task<ActionResult<IReadOnlyList<ReactivationEligibleDto>>> Eligible(CancellationToken ct)
    {
        return Ok(await links.ListEligibleAsync(ct));
    }

    [HttpPost("links")]
    public async Task<ActionResult<ReactivationGenerateResultDto>> Generate(
        [FromBody] ReactivationGenerateBody? body,
        CancellationToken ct)
    {
        var allEligible = body?.AllEligible == true;
        var ids = body?.HonoreeIds;
        if (!allEligible && (ids is null || ids.Count == 0))
        {
            return BadRequest(new { message = "Select at least one honoree." });
        }

        var rows = await links.GenerateAsync(ids, allEligible, Actor(), BaseUrl(), ct);
        return Ok(new ReactivationGenerateResultDto(ReactivationCsv.Write(rows), rows));
    }

    [HttpPost("links/csv")]
    [RequestSizeLimit(1_000_000)]
    public async Task<ActionResult<ReactivationGenerateResultDto>> GenerateFromCsv(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "Choose a CSV file with a honoree id column." });
        }

        if (file.Length > 1_000_000)
        {
            return BadRequest(new { message = "The CSV file is too large." });
        }

        string text;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            text = await reader.ReadToEndAsync(ct);
        }

        var rows = await links.GenerateFromCsvAsync(text, Actor(), BaseUrl(), ct);
        return Ok(new ReactivationGenerateResultDto(ReactivationCsv.Write(rows), rows));
    }

    [HttpPost("revoke")]
    public async Task<ActionResult<ReactivationRevokeBulkResultDto>> RevokeMany(
        [FromBody] ReactivationRevokeBody? body,
        CancellationToken ct)
    {
        var all = body?.All == true;
        var ids = body?.HonoreeIds;
        if (!all && (ids is null || ids.Count == 0))
        {
            return BadRequest(new { message = "Select at least one honoree, or revoke all active links." });
        }

        var count = await links.RevokeManyAsync(ids, all, ct);
        return Ok(new ReactivationRevokeBulkResultDto(count));
    }

    [HttpPost("{honoreeId:int}/revoke")]
    public async Task<ActionResult<ReactivationRevokeResultDto>> Revoke(int honoreeId, CancellationToken ct)
    {
        var revoked = await links.RevokeAsync(honoreeId, ct);
        return Ok(new ReactivationRevokeResultDto(revoked));
    }

    private string Actor()
    {
        var email = User.GetEmail();
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        return User.GetDisplayName() ?? "PFOH.Admin";
    }

    private string BaseUrl() => ReactivationLinkService.PublicBaseUrl(configuration, Request);
}
