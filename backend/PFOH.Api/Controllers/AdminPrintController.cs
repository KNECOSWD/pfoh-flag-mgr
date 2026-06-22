using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PFOH.Api.Data;
using PFOH.Api.Dtos;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/print")]
[Authorize(Policy = "AdminOnly")]
public class AdminPrintController(PfohDbContext db, IHttpClientFactory httpClientFactory) : ControllerBase
{
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

        var honoreeIds = changes
            .Where(r => r.HonoreeId.HasValue)
            .Select(r => r.HonoreeId!.Value)
            .Distinct()
            .ToList();

        var pdfLookup = await db.HonoreeSearchResults
            .AsNoTracking()
            .Where(h => honoreeIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, h => h.PDFUrl, ct);

        var missingPdf = changes
            .Where(r => !r.HonoreeId.HasValue ||
                        !pdfLookup.TryGetValue(r.HonoreeId.Value, out var url) ||
                        string.IsNullOrWhiteSpace(url))
            .Select(r => $"{r.FirstName} {r.LastName}".Trim())
            .ToList();

        if (missingPdf.Count > 0)
        {
            return BadRequest($"Missing PDF URL for: {string.Join(", ", missingPdf)}");
        }

        using var outputDocument = new PdfDocument();
        var httpClient = httpClientFactory.CreateClient();

        foreach (var change in changes)
        {
            var pdfUrl = pdfLookup[change.HonoreeId!.Value]!;

            await using var remoteStream = await httpClient.GetStreamAsync(pdfUrl, ct);
            using var memory = new MemoryStream();
            await remoteStream.CopyToAsync(memory, ct);
            memory.Position = 0;

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
}
