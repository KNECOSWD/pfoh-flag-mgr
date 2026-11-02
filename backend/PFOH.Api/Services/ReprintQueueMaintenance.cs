using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public class ReprintQueueMaintenance(PfohDbContext db)
{
    public async Task DeactivateDuplicateActiveReprintQueueItemsAsync(string adminName, CancellationToken ct)
    {
        // This pass only inserts Flagged rows. It does not edit queue items.
        // DeactivateOtherActiveReprintItemsForHonoreeAsync is unchanged and is
        // what an administrator runs for one item from Print Center Approve.
        _ = adminName;
        await FlagDuplicateActiveReprintItemsAsync(ct);
    }

    public async Task FlagDuplicateActiveReprintItemsAsync(CancellationToken ct)
    {
        var active = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(request =>
                request.HonoreeId.HasValue &&
                request.RequestStatus == "Approved" &&
                request.RequiresCardReprint &&
                request.CardPrintedUtc == null)
            .Select(request => new
            {
                request.Id,
                HonoreeId = request.HonoreeId!.Value,
                When = request.ReviewedUtc ?? request.SubmittedUtc ?? request.CreatedUtc
            })
            .ToListAsync(ct);

        var known = await db.ReprintDuplicateReviews
            .Select(review => review.DuplicateChangeRequestId)
            .ToListAsync(ct);
        var seen = known.ToHashSet();
        var now = DateTime.UtcNow;
        var added = false;

        foreach (var group in active.GroupBy(item => item.HonoreeId).Where(group => group.Count() > 1))
        {
            var ordered = group
                .OrderByDescending(item => item.When)
                .ThenByDescending(item => item.Id)
                .ToList();
            var keep = ordered[0];
            foreach (var duplicate in ordered.Skip(1))
            {
                if (!seen.Add(duplicate.Id))
                {
                    continue;
                }

                db.ReprintDuplicateReviews.Add(new ReprintDuplicateReview
                {
                    HonoreeId = group.Key,
                    DuplicateChangeRequestId = duplicate.Id,
                    KeptChangeRequestId = keep.Id,
                    Status = ReprintDuplicateReviewStatus.Flagged,
                    FlaggedUtc = now
                });
                added = true;
            }
        }

        if (added)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task DeactivateOtherActiveReprintItemsForHonoreeAsync(
        int honoreeId,
        int? keepChangeRequestId,
        string adminName,
        CancellationToken ct,
        string? actorObjectId = null)
    {
        var activeItems = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .Where(request =>
                request.HonoreeId == honoreeId &&
                request.RequestStatus == "Approved" &&
                request.RequiresCardReprint &&
                request.CardPrintedUtc == null)
            .OrderByDescending(request => request.ReviewedUtc ?? request.SubmittedUtc ?? request.CreatedUtc)
            .ThenByDescending(request => request.Id)
            .ToListAsync(ct);

        if (activeItems.Count <= 1)
        {
            return;
        }

        var keepItem = keepChangeRequestId.HasValue
            ? activeItems.FirstOrDefault(request => request.Id == keepChangeRequestId.Value)
            : activeItems.FirstOrDefault();

        keepItem ??= activeItems[0];

        var now = DateTime.UtcNow;
        var duplicates = activeItems.Where(request => request.Id != keepItem.Id).ToList();
        if (duplicates.Count > 0)
        {
            await ReprintQueueEventSchema.EnsureAsync(db, ct);
        }
        foreach (var duplicate in duplicates)
        {
            ApplyDuplicateRemoval(duplicate, keepItem.Id, adminName, now);
            ReprintQueueAudit.Add(
                db,
                duplicate.Id,
                duplicate.HonoreeId,
                ReprintQueueActions.DedupRemove,
                adminName,
                actorObjectId,
                now,
                $"Removed duplicate reprint queue item. Active queue item #{keepItem.Id} remains for this honoree.",
                "dedup",
                duplicates.Count);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveOneDuplicateReprintItemAsync(
        int duplicateChangeRequestId,
        int keepChangeRequestId,
        string adminName,
        CancellationToken ct,
        string? actorObjectId = null)
    {
        if (duplicateChangeRequestId == keepChangeRequestId)
        {
            return;
        }

        var duplicate = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .FirstOrDefaultAsync(request => request.Id == duplicateChangeRequestId, ct);
        if (duplicate is null ||
            duplicate.RequestStatus != "Approved" ||
            !duplicate.RequiresCardReprint ||
            duplicate.CardPrintedUtc != null)
        {
            return;
        }

        await ReprintQueueEventSchema.EnsureAsync(db, ct);
        var now = DateTime.UtcNow;
        ApplyDuplicateRemoval(duplicate, keepChangeRequestId, adminName, now);
        ReprintQueueAudit.Add(
            db,
            duplicate.Id,
            duplicate.HonoreeId,
            ReprintQueueActions.DedupRemove,
            adminName,
            actorObjectId,
            now,
            $"Removed duplicate reprint queue item. Active queue item #{keepChangeRequestId} remains for this honoree.",
            "dedup",
            1);
        await db.SaveChangesAsync(ct);
    }

    private static void ApplyDuplicateRemoval(
        HonoreeChangeRequest duplicate,
        int keepChangeRequestId,
        string adminName,
        DateTime now)
    {
        duplicate.RequiresCardReprint = false;
        duplicate.ReviewedBy = adminName;
        duplicate.ReviewedUtc = now;
        var note = $"Removed duplicate reprint queue item. Active queue item #{keepChangeRequestId} remains for this honoree.";
        duplicate.ReviewNotes = string.IsNullOrWhiteSpace(duplicate.ReviewNotes)
            ? note
            : $"{duplicate.ReviewNotes}\n{note}";

        if (duplicate.FlagClaim?.ClaimStatus == "AdminReprintQueued")
        {
            duplicate.FlagClaim.ClaimStatus = "AdminReprintDuplicateRemoved";
            duplicate.FlagClaim.AdminNotes = string.IsNullOrWhiteSpace(duplicate.FlagClaim.AdminNotes)
                ? note
                : $"{duplicate.FlagClaim.AdminNotes}\n{note}";
        }
    }
}
