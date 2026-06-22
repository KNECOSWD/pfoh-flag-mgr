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

    [HttpGet("{honoreeId:int}/pdf")]
    public async Task<IActionResult> OpenOrCreatePdf(int honoreeId, CancellationToken ct)
    {
        var storedPdf = await fileStorage.DownloadPdfAsync(honoreeId, ct);
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

        var photoBytes = await fileStorage.DownloadImageAsync(honoree.PhotoFileName, searchResult?.ImageUrl, ct);
        var pdf = HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes);
        await fileStorage.UploadPdfAsync(honoree.Id, pdf, ct);

        Response.Headers["Content-Disposition"] = $"inline; filename=\"{fileStorage.GetHonoreePdfFileName(honoree.Id)}\"";
        Response.Headers["X-PFOH-PDF-Source"] = "generated";

        return File(pdf, "application/pdf");
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
