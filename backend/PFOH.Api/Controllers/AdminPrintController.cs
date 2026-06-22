using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/print")]
[Authorize(Policy = "AdminOnly")]
public class AdminPrintController(
    PfohDbContext db,
    IConfiguration configuration,
    IWebHostEnvironment environment) : ControllerBase
{
    private readonly HonoreeFileStorage fileStorage = new(configuration);

    [HttpPost("merge")]
    public async Task<IActionResult> MergeApprovedCards(
        [FromBody] BulkPrintRequest request,
        CancellationToken ct)
    {
        if (request.ChangeRequestIds.Count == 0)
        {
            return BadRequest("Select at least one approved item to print.");
        }

        var changes = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(r =>
                request.ChangeRequestIds.Contains(r.Id) &&
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint &&
                r.HonoreeId != null)
            .OrderBy(r => r.LastName)
            .ThenBy(r => r.FirstName)
            .ToListAsync(ct);

        if (changes.Count == 0)
        {
            return BadRequest("No approved card reprint items were found.");
        }

        using var outputDocument = new PdfDocument();

        foreach (var change in changes)
        {
            var pdfBytes = await GetOrCreatePdfBytesAsync(change.HonoreeId!.Value, ct);
            using var memory = new MemoryStream(pdfBytes);
            using var inputDocument = PdfReader.Open(memory, PdfDocumentOpenMode.Import);

            for (var idx = 0; idx < inputDocument.PageCount; idx++)
            {
                outputDocument.AddPage(inputDocument.Pages[idx]);
            }
        }

        using var output = new MemoryStream();
        outputDocument.Save(output, false);

        var fileName = $"pfoh-card-reprints-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf";
        return File(output.ToArray(), "application/pdf", fileName);
    }

    [HttpPost("mark-printed")]
    public async Task<IActionResult> MarkPrinted(
        [FromBody] BulkPrintRequest request,
        CancellationToken ct)
    {
        if (request.ChangeRequestIds.Count == 0)
        {
            return BadRequest("Select at least one item to mark printed.");
        }

        var batchId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var changes = await db.HonoreeChangeRequests
            .Where(r =>
                request.ChangeRequestIds.Contains(r.Id) &&
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint)
            .ToListAsync(ct);

        foreach (var change in changes)
        {
            change.CardPrintedUtc = now;
            change.CardPrintBatchId = batchId;
        }

        await db.SaveChangesAsync(ct);

        return Ok(new { batchId, count = changes.Count });
    }

    private async Task<byte[]> GetOrCreatePdfBytesAsync(int honoreeId, CancellationToken ct)
    {
        var existing = await fileStorage.DownloadPdfAsync(honoreeId, ct);
        if (existing is not null)
        {
            return existing;
        }

        var honoree = await db.Honorees
            .AsNoTracking()
            .Include(h => h.FlagGrid)
            .Include(h => h.ServiceBranch)
            .Include(h => h.Sponsor)
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive && h.DeletedDate == null, ct)
            ?? throw new InvalidOperationException($"Honoree {honoreeId} was not found.");

        var searchResult = await db.HonoreeSearchResults
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive, ct);

        var photoBytes = await fileStorage.DownloadImageAsync(honoree.PhotoFileName, searchResult?.ImageUrl, ct);
        var pdf = HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes);
        await fileStorage.UploadPdfAsync(honoreeId, pdf, ct);
        return pdf;
    }
}
