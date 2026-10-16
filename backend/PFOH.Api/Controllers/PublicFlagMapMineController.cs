using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PFOH.Api.Extensions;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting("PublicFlagMap")]
[Route("api/public/flag-map")]
public class PublicFlagMapMineController(
    PublicFlagMap map,
    UserProfileService profiles,
    ILogger<PublicFlagMapMineController> logger) : ControllerBase
{
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var displayName = await profiles.ResolveDisplayNameAsync(User, ct);
        var flags = await map.ForCurrentSubmitterAsync(User.GetExternalObjectId(), displayName, ct);
        logger.LogInformation(
            "Public flag map current submitter from {ClientIp} outcome {Outcome} count {Count}",
            PublicFlagMapClientIp.Resolve(HttpContext),
            flags.Count == 0 ? "none" : "ok",
            flags.Count);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(new { flags });
    }
}
