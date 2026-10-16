using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    HonoreeFileStorage fileStorage,
    IConfiguration configuration,
    ILogger<PublicFlagMapController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("sections")]
    public async Task<IActionResult> Sections(CancellationToken ct)
    {
        return CachedJson(await map.SectionsAsync(ct), 60);
    }

    [HttpGet("sections/{section}")]
    public async Task<IActionResult> Section(string section, CancellationToken ct)
    {
        var seats = await map.SeatsAsync(section, ct);
        if (seats is null)
        {
            return NotFound();
        }

        return CachedJson(new { section = seats[0].Section, seats }, 60);
    }

    [HttpGet("search-index")]
    public async Task<IActionResult> SearchIndex(CancellationToken ct)
    {
        return CachedJson(await map.SearchIndexAsync(ct), 60);
    }

    [HttpGet("by-submitter")]
    [EnableRateLimiting("PublicFlagMapBySubmitter")]
    public async Task<IActionResult> BySubmitter([FromQuery] string? first, [FromQuery] string? last, CancellationToken ct)
    {
        var result = await map.BySubmitterAsync(first, last, ct);
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
    public async Task<IActionResult> Logo(string fileName, CancellationToken ct)
    {
        if (!await map.IsPublishedLogoAsync(fileName, ct))
        {
            return NotFound();
        }

        var bytes = await fileStorage.DownloadServiceLogoAsync(fileName, ct);
        if (bytes is null || bytes.Length == 0)
        {
            return NotFound();
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Response.Headers.ETag = $"\"{hash}\"";
        Response.Headers.CacheControl = "public, max-age=86400";
        if (Request.Headers.IfNoneMatch.Any(tag => tag?.Contains(hash, StringComparison.Ordinal) == true))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(bytes, LogoContentType(fileName));
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

    private IActionResult CachedJson(object payload, int maxAgeSeconds)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        Response.Headers.ETag = $"\"{hash}\"";
        Response.Headers.CacheControl = $"public, max-age={maxAgeSeconds}";
        if (Request.Headers.IfNoneMatch.Any(tag => tag?.Contains(hash, StringComparison.Ordinal) == true))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(json, "application/json");
    }

    private static string LogoContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }
}
