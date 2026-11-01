using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
[EnableRateLimiting("AuthenticatedGet")]
public class AdminPrintController(
    PfohDbContext db,
    HonoreeFileStorage fileStorage,
    ReprintQueueMaintenance reprintQueue,
    HonoreePdfService pdfs) : ControllerBase
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

    // One request per card in Download merged PDF. Not part of the 60/min cap.
    [DisableRateLimiting]
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
        var honoree = await db.Honorees.AsNoTracking().FirstOrDefaultAsync(item => item.Id == honoreeId, ct);
        if (honoree is null || !honoree.IsActive || honoree.DeletedDate != null)
        {
            return NotFound(new
            {
                code = "honoree-inactive",
                changeRequestId,
                honoreeId,
                honoreeName,
                message = $"{honoreeName} is not an active honoree, so the card PDF is not available."
            });
        }

        var opened = await pdfs.OpenAsync(honoreeId, ct);
        if (opened.StorageFailed)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                code = "storage-read-failed",
                changeRequestId,
                honoreeId,
                honoreeName,
                message = $"The stored card PDF {fileName} could not be read: {opened.Message}"
            });
        }

        if (opened.RebuildFailed || opened.Bytes is null || opened.Bytes.Length == 0)
        {
            return Conflict(new
            {
                code = "pdf-outdated",
                changeRequestId,
                honoreeId,
                honoreeName,
                message = $"The card for {honoreeName} is out of date and could not be rebuilt. It was not printed from an older file."
            });
        }

        HonoreePdfService.ApplyFreshnessHeaders(Response, opened.Stamp);
        return File(opened.Bytes, "application/pdf", fileName);
    }

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<CardPrintEventDto>>> History(CancellationToken ct)
    {
        await CardPrintEventSchema.EnsureAsync(db, ct);
        await ReprintQueueEventSchema.EnsureAsync(db, ct);
        var events = await db.CardPrintEvents
            .AsNoTracking()
            .Where(e => e.CardCount > 0)
            .OrderByDescending(e => e.PrintedUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => new CardPrintEventDto(e.CardPrintBatchId, e.PrintedUtc, e.PrintedBy, e.CardCount, e.Comment))
            .ToListAsync(ct);
        var removed = await db.ReprintQueueEvents
            .AsNoTracking()
            .Where(e => e.Action == ReprintQueueActions.Remove || e.Action == ReprintQueueActions.DedupRemove)
            .OrderByDescending(e => e.Utc)
            .ThenByDescending(e => e.Id)
            .Take(200)
            .Select(e => new ReprintRemovalHistoryDto(
                e.Id,
                e.ChangeRequestId,
                e.HonoreeId,
                e.Action,
                e.ActorEmail,
                e.Utc,
                e.Reason,
                e.Source,
                e.BatchSize))
            .ToListAsync(ct);
        return Ok(new PrintHistoryDto(events, removed));
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

        var actor = AdminActor.From(User);
        var printedBy = actor.Email;

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
            ReprintQueueAudit.Add(
                db,
                change.Id,
                change.HonoreeId,
                ReprintQueueActions.Printed,
                actor.Email,
                actor.ObjectId,
                now,
                comment,
                "print",
                changes.Count);
        }

        await CardPrintEventSchema.EnsureAsync(db, ct);
        await ReprintQueueEventSchema.EnsureAsync(db, ct);
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

    [HttpGet("duplicate-reviews")]
    public async Task<ActionResult<IReadOnlyList<ReprintDuplicateReviewDto>>> DuplicateReviews(CancellationToken ct)
    {
        await ReprintDuplicateReviewSchema.EnsureAsync(db, ct);
        await reprintQueue.FlagDuplicateActiveReprintItemsAsync(ct);

        var open = await db.ReprintDuplicateReviews
            .AsNoTracking()
            .Where(review => review.Status == ReprintDuplicateReviewStatus.Flagged && review.ResolvedUtc == null)
            .OrderBy(review => review.FlaggedUtc)
            .ThenBy(review => review.Id)
            .ToListAsync(ct);

        var honoreeIds = open.Select(review => review.HonoreeId).Distinct().ToList();
        var namedHonorees = honoreeIds.Count == 0
            ? new List<Honoree>()
            : await db.Honorees
                .AsNoTracking()
                .Where(honoree => honoreeIds.Contains(honoree.Id))
                .ToListAsync(ct);
        var names = namedHonorees.ToDictionary(honoree => honoree.Id, AdminDuplicatesController.DisplayName);

        return Ok(open.Select(review => new ReprintDuplicateReviewDto(
            review.Id,
            review.HonoreeId,
            names.TryGetValue(review.HonoreeId, out var name) ? name : $"Honoree {review.HonoreeId}",
            review.DuplicateChangeRequestId,
            review.KeptChangeRequestId,
            review.FlaggedUtc)).ToList());
    }

    [HttpPost("duplicate-reviews/{id:int}/approve")]
    public async Task<IActionResult> ApproveDuplicateReview(int id, CancellationToken ct)
    {
        var review = await OpenReviewAsync(id, ct);
        if (review is null)
        {
            return NotFound(new { message = "That flagged duplicate is no longer waiting for review." });
        }

        var actor = AdminActor.From(User);
        var adminName = actor.Email;
        await reprintQueue.RemoveOneDuplicateReprintItemAsync(
            review.DuplicateChangeRequestId,
            review.KeptChangeRequestId,
            adminName,
            ct,
            actor.ObjectId);
        review.ResolvedUtc = DateTime.UtcNow;
        review.ResolvedBy = adminName;
        await db.SaveChangesAsync(ct);
        return Ok(new { message = "The duplicate reprint was removed. The other card stays in the queue." });
    }

    [HttpPost("duplicate-reviews/{id:int}/dismiss")]
    public async Task<IActionResult> DismissDuplicateReview(int id, CancellationToken ct)
    {
        var review = await OpenReviewAsync(id, ct);
        if (review is null)
        {
            return NotFound(new { message = "That flagged duplicate is no longer waiting for review." });
        }

        review.Status = ReprintDuplicateReviewStatus.Dismissed;
        review.ResolvedUtc = DateTime.UtcNow;
        review.ResolvedBy = AdminName();
        await db.SaveChangesAsync(ct);
        return Ok(new { message = "Dismissed. Both cards stay in the queue." });
    }

    private async Task<ReprintDuplicateReview?> OpenReviewAsync(int id, CancellationToken ct)
    {
        var review = await db.ReprintDuplicateReviews.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (review is null || review.Status != ReprintDuplicateReviewStatus.Flagged || review.ResolvedUtc is not null)
        {
            return null;
        }

        return review;
    }

    private string AdminName()
    {
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        return adminName;
    }

    private IQueryable<Models.HonoreeChangeRequest> QueuedCards() =>
        db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(r =>
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint &&
                r.HonoreeId != null);
}

public sealed record ReprintDuplicateReviewDto(
    int Id,
    int HonoreeId,
    string HonoreeName,
    int DuplicateChangeRequestId,
    int KeptChangeRequestId,
    DateTime FlaggedUtc);
