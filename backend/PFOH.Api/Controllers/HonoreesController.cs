using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/honorees")]
[AllowAnonymous]
public class HonoreesController(
    PfohDbContext db,
    IConfiguration configuration,
    IWebHostEnvironment environment) : ControllerBase
{
    private static readonly HttpClient Http = new();
    private readonly HonoreeFileStorage fileStorage = new(configuration);

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

    [HttpGet("{honoreeId:int}/photo")]
    public async Task<IActionResult> GetPhoto(int honoreeId, CancellationToken ct)
    {
        var honoree = await db.Honorees
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive && h.DeletedDate == null, ct);

        if (honoree is null)
        {
            return NotFound("Honoree was not found.");
        }

        var searchResult = await db.HonoreeSearchResults
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive, ct);

        var photoBytes = await SafeDownloadImageAsync(honoree.PhotoFileName, searchResult?.ImageUrl, ct);

        if (photoBytes is not null && photoBytes.Length > 0)
        {
            var contentType = GuessImageContentType(honoree.PhotoFileName, searchResult?.ImageUrl);
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";

            return File(photoBytes, contentType);
        }

        var publicUrl = BuildPublicImageUrl(honoree.PhotoFileName, searchResult?.ImageUrl);
        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            return Redirect(publicUrl);
        }

        return NotFound("Honoree photo was not found.");
    }

    [HttpGet("{honoreeId:int}/pdf")]
    public async Task<IActionResult> OpenOrCreatePdf(int honoreeId, CancellationToken ct)
    {
        var storedPdf = await SafeDownloadPdfAsync(honoreeId, ct);
        if (storedPdf is not null)
        {
            return File(storedPdf, "application/pdf", fileStorage.GetHonoreePdfFileName(honoreeId));
        }

        var honoree = await db.Honorees
            .AsNoTracking()
            .Include(h => h.FlagGrid)
            .Include(h => h.ServiceBranch)
            .Include(h => h.Sponsor)
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive && h.DeletedDate == null, ct);

        if (honoree is null)
        {
            return NotFound("Honoree was not found.");
        }

        var searchResult = await db.HonoreeSearchResults
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive, ct);

        if (!string.IsNullOrWhiteSpace(searchResult?.PDFUrl) &&
            await ExistingPdfIsAvailableAsync(searchResult.PDFUrl, ct))
        {
            return Redirect(searchResult.PDFUrl);
        }

        var photoBytes = await SafeDownloadImageAsync(honoree.PhotoFileName, searchResult?.ImageUrl, ct);
        var pdf = HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes);

        // Return the generated PDF even if storage upload fails. This prevents users
        // from seeing a browser 500 when a blob setting/container is temporarily wrong.
        await SafeUploadPdfAsync(honoree.Id, pdf, ct);

        Response.Headers["Content-Disposition"] = $"inline; filename=\"{fileStorage.GetHonoreePdfFileName(honoree.Id)}\"";
        Response.Headers["X-PFOH-PDF-Source"] = "generated";

        return File(pdf, "application/pdf");
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("{honoreeId:int}/pdf/regenerate")]
    public async Task<ActionResult<object>> RegeneratePdf(int honoreeId, CancellationToken ct)
    {
        var honoree = await db.Honorees
            .AsNoTracking()
            .Include(h => h.FlagGrid)
            .Include(h => h.ServiceBranch)
            .Include(h => h.Sponsor)
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive && h.DeletedDate == null, ct);

        if (honoree is null)
        {
            return NotFound("Honoree was not found.");
        }

        var searchResult = await db.HonoreeSearchResults
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive, ct);

        var photoBytes = await SafeDownloadImageAsync(honoree.PhotoFileName, searchResult?.ImageUrl, ct);
        var pdf = HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes);
        var fileName = await fileStorage.UploadPdfAsync(honoree.Id, pdf, ct);

        return Ok(new
        {
            honoreeId = honoree.Id,
            fileName,
            generatedUtc = DateTime.UtcNow
        });
    }

    private string? BuildPublicImageUrl(string? photoFileName, string? fallbackImageUrl)
    {
        if (!string.IsNullOrWhiteSpace(fallbackImageUrl))
        {
            return fallbackImageUrl;
        }

        if (string.IsNullOrWhiteSpace(photoFileName))
        {
            return null;
        }

        var baseUrl =
            configuration["BlobStorage:PublicImageBaseUrl"] ??
            configuration["BlobStorage:ImageBaseUrl"] ??
            "https://pfohimages.blob.core.windows.net/honoreeimages";

        return $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(photoFileName)}";
    }

    private static string GuessImageContentType(string? photoFileName, string? fallbackUrl)
    {
        var source = !string.IsNullOrWhiteSpace(photoFileName)
            ? photoFileName
            : fallbackUrl ?? string.Empty;

        return Path.GetExtension(source).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/jpeg"
        };
    }

    private async Task<byte[]?> SafeDownloadPdfAsync(int honoreeId, CancellationToken ct)
    {
        try
        {
            return await fileStorage.DownloadPdfAsync(honoreeId, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> SafeDownloadImageAsync(
        string? photoFileName,
        string? fallbackImageUrl,
        CancellationToken ct)
    {
        try
        {
            var imageBytes = await fileStorage.DownloadImageAsync(photoFileName, fallbackImageUrl, ct);
            if (imageBytes is not null && imageBytes.Length > 0)
            {
                return imageBytes;
            }
        }
        catch
        {
            // Fall through to public URL fallback below.
        }

        var publicUrl = BuildPublicImageUrl(photoFileName, fallbackImageUrl);
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            return null;
        }

        try
        {
            return await Http.GetByteArrayAsync(publicUrl, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task SafeUploadPdfAsync(int honoreeId, byte[] pdf, CancellationToken ct)
    {
        try
        {
            await fileStorage.UploadPdfAsync(honoreeId, pdf, ct);
        }
        catch
        {
            // Still return the generated PDF to the user.
            // Blob settings/container access can be fixed separately.
        }
    }

    private async Task<bool> ExistingPdfIsAvailableAsync(string pdfUrl, CancellationToken ct)
    {
        if (!TryBuildAbsoluteUri(pdfUrl, out var uri))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private bool TryBuildAbsoluteUri(string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!))
        {
            return true;
        }

        var relativeUrl = url.StartsWith('/') ? url : $"/{url}";
        return Uri.TryCreate($"{Request.Scheme}://{Request.Host}{relativeUrl}", UriKind.Absolute, out uri!);
    }
}
