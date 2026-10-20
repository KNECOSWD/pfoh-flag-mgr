using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Models;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/print")]
[Authorize(Policy = "AdminOnly")]
public class AdminPrintController(
    PfohDbContext db,
    HonoreeFileStorage fileStorage) : ControllerBase
{
    [HttpPost("plan")]
    public async Task<ActionResult<PrintBatchPlanDto>> PlanBatch(
        [FromBody] BulkPrintRequest request,
        CancellationToken ct)
    {
        if (request.ChangeRequestIds.Count == 0)
        {
            return BadRequest(new { message = "Select at least one approved item to print." });
        }

        var requestedIds = request.ChangeRequestIds.Distinct().ToList();
        var changes = await QueuedCards()
            .Where(r => requestedIds.Contains(r.Id))
            .OrderBy(r => r.LastName)
            .ThenBy(r => r.FirstName)
            .ToListAsync(ct);

        var cards = changes
            .Select(r => new PrintBatchCardDto(r.Id, r.HonoreeId!.Value, AdminReviewController.BuildHonoreeName(r)))
            .ToList();
        var found = cards.Select(c => c.ChangeRequestId).ToHashSet();
        var notInQueue = requestedIds.Where(id => !found.Contains(id)).ToList();

        return Ok(new PrintBatchPlanDto(cards, notInQueue));
    }

    [HttpGet("cards/{changeRequestId:int}/pdf")]
    public async Task<IActionResult> GetQueuedCardPdf(int changeRequestId, CancellationToken ct)
    {
        var change = await QueuedCards().FirstOrDefaultAsync(r => r.Id == changeRequestId, ct);
        if (change is null)
        {
            return NotFound(new
            {
                code = "not-in-queue",
                changeRequestId,
                message = "This card is no longer an approved reprint queue item."
            });
        }

        var honoreeId = change.HonoreeId!.Value;
        var honoreeName = AdminReviewController.BuildHonoreeName(change);
        var fileName = fileStorage.GetHonoreePdfFileName(honoreeId);
        byte[]? pdf;

        try
        {
            pdf = await fileStorage.DownloadPdfAsync(honoreeId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                code = "storage-read-failed",
                changeRequestId,
                honoreeId,
                honoreeName,
                message = $"The stored card PDF {fileName} could not be read: {ex.Message}"
            });
        }

        if (pdf is null || pdf.Length == 0)
        {
            return NotFound(new
            {
                code = "pdf-missing",
                changeRequestId,
                honoreeId,
                honoreeName,
                message = $"No stored card PDF {fileName} was found."
            });
        }

        return File(pdf, "application/pdf", fileName);
    }

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<CardPrintEventDto>>> History(CancellationToken ct)
    {
        await CardPrintEventSchema.EnsureAsync(db, ct);
        var events = await db.CardPrintEvents
            .AsNoTracking()
            .Where(e => e.CardCount > 0)
            .OrderByDescending(e => e.PrintedUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => new CardPrintEventDto(e.CardPrintBatchId, e.PrintedUtc, e.PrintedBy, e.CardCount, e.Comment))
            .ToListAsync(ct);
        return Ok(events);
    }

    [HttpPost("mark-printed")]
    public async Task<IActionResult> MarkPrinted(
        [FromBody] MarkPrintedRequest request,
        CancellationToken ct)
    {
        if (request.ChangeRequestIds.Count == 0)
        {
            return BadRequest("Select at least one item to mark printed.");
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > CardPrintEventLimits.CommentMaxLength })
        {
            return BadRequest($"Comments must be {CardPrintEventLimits.CommentMaxLength} characters or fewer.");
        }

        var printedBy = User.GetEmail();
        if (string.IsNullOrWhiteSpace(printedBy))
        {
            printedBy = User.GetDisplayName() ?? "PFOH.Admin";
        }

        if (printedBy.Length > CardPrintEventLimits.PrintedByMaxLength)
        {
            printedBy = printedBy[..CardPrintEventLimits.PrintedByMaxLength];
        }

        var batchId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var changes = await db.HonoreeChangeRequests
            .Where(r =>
                request.ChangeRequestIds.Contains(r.Id) &&
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint)
            .ToListAsync(ct);

        if (changes.Count == 0)
        {
            return Ok(new { batchId, count = 0 });
        }

        foreach (var change in changes)
        {
            change.CardPrintedUtc = now;
            change.CardPrintBatchId = batchId;
        }

        await CardPrintEventSchema.EnsureAsync(db, ct);
        db.CardPrintEvents.Add(new CardPrintEvent
        {
            CardPrintBatchId = batchId,
            PrintedBy = printedBy,
            PrintedUtc = now,
            CardCount = changes.Count,
            Comment = comment
        });

        await db.SaveChangesAsync(ct);

        return Ok(new { batchId, count = changes.Count });
    }

    private IQueryable<Models.HonoreeChangeRequest> QueuedCards() =>
        db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(r =>
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint &&
                r.HonoreeId != null);
}
