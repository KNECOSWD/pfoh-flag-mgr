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
    IConfiguration configuration) : ControllerBase
{
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
        var rows = await links.GenerateAsync(body?.HonoreeIds, Actor(), BaseUrl(), ct);
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
