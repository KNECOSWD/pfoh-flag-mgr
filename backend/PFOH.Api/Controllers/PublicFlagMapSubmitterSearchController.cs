using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PFOH.Api.Dtos;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("PublicFlagMapSubmitterSearch")]
[Route("api/public/flag-map")]
public class PublicFlagMapSubmitterSearchController(
    PublicFlagMap map,
    SubmitterNameTokenCache names,
    SubmitterSuggestionTokens tokens,
    ILogger<PublicFlagMapSubmitterSearchController> logger) : ControllerBase
{
    [HttpGet("submitter-suggestions")]
    public IActionResult SuggestionsGet()
    {
        SubmitterLookupPrivacy.ApplyResponseHeaders(Response);
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    [HttpPost("submitter-suggestions")]
    public async Task<IActionResult> Suggestions([FromBody] SubmitterSuggestionRequest? request, CancellationToken ct)
    {
        var result = await names.SuggestAsync(request?.Query, ct);
        logger.LogInformation(
            "Public flag map submitter suggestions from {ClientIp} outcome {Outcome} count {Count}",
            PublicFlagMapClientIp.Resolve(HttpContext),
            result.Outcome,
            result.Names.Count);
        SubmitterLookupPrivacy.ApplyResponseHeaders(Response);
        if (result.Outcome == "rejected")
        {
            return BadRequest(new { message = "Enter at least 3 letters." });
        }

        var suggestions = result.Names.Select(name => new
        {
            label = "All flags submitted by " + name.Display,
            token = tokens.Issue(name.NormalizedFirst, name.NormalizedLast)
        });
        return Ok(new { suggestions });
    }

    [HttpGet("submitter-flags")]
    public IActionResult FlagsGet()
    {
        SubmitterLookupPrivacy.ApplyResponseHeaders(Response);
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    [HttpPost("submitter-flags")]
    public async Task<IActionResult> Flags([FromBody] SubmitterTokenRequest? request, CancellationToken ct)
    {
        SubmitterLookupPrivacy.ApplyResponseHeaders(Response);
        if (!tokens.TryRead(request?.Token, out var first, out var last))
        {
            logger.LogInformation(
                "Public flag map submitter flags from {ClientIp} outcome {Outcome} count {Count}",
                PublicFlagMapClientIp.Resolve(HttpContext),
                "none",
                0);
            return NotFound(new { message = "No flags found" });
        }

        var flags = await map.FlagsForExactSubmitterAsync(first, last, ct);
        logger.LogInformation(
            "Public flag map submitter flags from {ClientIp} outcome {Outcome} count {Count}",
            PublicFlagMapClientIp.Resolve(HttpContext),
            flags.Count == 0 ? "none" : "ok",
            flags.Count);
        if (flags.Count == 0)
        {
            return NotFound(new { message = "No flags found" });
        }

        return Ok(new { flags });
    }
}
