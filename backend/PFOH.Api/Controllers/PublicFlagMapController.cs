using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PFOH.Api.Dtos;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("PublicFlagMap")]
[Route("api/public/flag-map")]
public class PublicFlagMapController(
    PublicFlagMap map,
    PublicFlagMapCache cache,
    PublicLogoCache logos,
    HonoreeFileStorage fileStorage,
    IConfiguration configuration,
    ILogger<PublicFlagMapController> logger) : ControllerBase
{
    [HttpGet("sections")]
    public async Task<IActionResult> Sections(CancellationToken ct)
    {
        var snapshot = await cache.GetAsync(ct);
        return CachedBody(snapshot.SectionsJson, snapshot.SectionsEtag, 60);
    }

    [HttpGet("field")]
    public async Task<IActionResult> Field(CancellationToken ct)
    {
        var snapshot = await cache.GetAsync(ct);
        return CachedBody(snapshot.FieldJson, snapshot.FieldEtag, 60);
    }

    [HttpGet("sections/{section}")]
    public async Task<IActionResult> Section(string section, CancellationToken ct)
    {
        var snapshot = await cache.GetAsync(ct);
        if (!snapshot.Sections.TryGetValue(section, out var payload))
        {
            return NotFound();
        }

        return CachedBody(payload.Json, payload.Etag, 60);
    }

    [HttpGet("search-index")]
    public async Task<IActionResult> SearchIndex(CancellationToken ct)
    {
        var snapshot = await cache.GetAsync(ct);
        return CachedBody(snapshot.SearchJson, snapshot.SearchEtag, 60);
    }

    [HttpGet("by-submitter")]
    [EnableRateLimiting("PublicFlagMapBySubmitter")]
    public IActionResult BySubmitterGet()
    {
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    [HttpPost("by-submitter")]
    [EnableRateLimiting("PublicFlagMapBySubmitter")]
    public async Task<IActionResult> BySubmitter([FromBody] SubmitterLookupRequest? request, CancellationToken ct)
    {
        var result = await map.BySubmitterAsync(request?.First, request?.Last, ct);
        logger.LogInformation(
            "Public flag map submitter lookup from {ClientIp} outcome {Outcome} count {Count}",
            PublicFlagMapClientIp.Resolve(HttpContext),
            result.Outcome,
            result.Count);

        if (result.Outcome == "rejected")
        {
            return BadRequest(new { message = "Enter at least 2 letters of the first name and 3 of the last name." });
        }

        if (result.Outcome == "refine")
        {
            return Ok(new { refine = true });
        }

        if (result.Outcome == "none")
        {
            return Ok(new { message = "No flags found" });
        }

        return Ok(new { flags = result.Flags });
    }

    [HttpGet("logos/{fileName}")]
    public async Task<IActionResult> Logo(string fileName, [FromQuery] int? w, [FromQuery] string? fmt, CancellationToken ct)
    {
        var snapshot = await cache.GetAsync(ct);
        if (!PublicFlagMap.IsPublishedLogo(fileName, snapshot.StoredLogoFileNames))
        {
            return NotFound();
        }

        var bytes = await fileStorage.DownloadServiceLogoAsync(fileName, ct);
        if (bytes is null || bytes.Length == 0)
        {
            return NotFound();
        }

        var webp = string.Equals(fmt, "webp", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(fmt)
                && Request.Headers.Accept.Any(value => value?.Contains("image/webp", StringComparison.OrdinalIgnoreCase) == true));
        var edge = w == DisplayImage.LogoEdgeSmall ? DisplayImage.LogoEdgeSmall : DisplayImage.LogoEdgeLarge;
        var encoded = logos.Get(fileName, edge, webp, bytes);
        Response.Headers.ETag = encoded.Etag;
        Response.Headers.CacheControl = StaticAssetCaching.ImmutableYear;
        Response.Headers.Vary = "Accept";
        var hash = encoded.Etag.Trim('"');
        if (Request.Headers.IfNoneMatch.Any(tag => tag?.Contains(hash, StringComparison.Ordinal) == true))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(encoded.Bytes, encoded.ContentType);
    }

    [HttpPost("kiosk-exit")]
    [EnableRateLimiting("PublicFlagMapKioskExit")]
    public IActionResult KioskExit([FromBody] KioskExitRequest? request)
    {
        if (KioskExitPin.ConfiguredValue(configuration) is null)
        {
            return NotFound();
        }

        if (!KioskExitPin.Matches(configuration, request?.Pin))
        {
            return Unauthorized();
        }

        return NoContent();
    }

    private IActionResult CachedBody(string json, string etag, int maxAgeSeconds)
    {
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = $"public, max-age={maxAgeSeconds}";
        var hash = etag.Trim('"');
        if (Request.Headers.IfNoneMatch.Any(tag => tag?.Contains(hash, StringComparison.Ordinal) == true))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(json, "application/json");
    }
}
