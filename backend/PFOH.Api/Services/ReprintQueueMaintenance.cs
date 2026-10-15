using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

public class ReprintQueueMaintenance(PfohDbContext db)
{
    public async Task DeactivateDuplicateActiveReprintQueueItemsAsync(string adminName, CancellationToken ct)
    {
        var duplicateHonoreeIds = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(request =>
                request.HonoreeId.HasValue &&
                request.RequestStatus == "Approved" &&
                request.RequiresCardReprint &&
                request.CardPrintedUtc == null)
            .GroupBy(request => request.HonoreeId!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToListAsync(ct);

        foreach (var honoreeId in duplicateHonoreeIds)
        {
            await DeactivateOtherActiveReprintItemsForHonoreeAsync(honoreeId, keepChangeRequestId: null, adminName, ct);
        }
    }

    public async Task DeactivateOtherActiveReprintItemsForHonoreeAsync(
        int honoreeId,
        int? keepChangeRequestId,
        string adminName,
        CancellationToken ct)
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
        foreach (var duplicate in activeItems.Where(request => request.Id != keepItem.Id))
        {
            duplicate.RequiresCardReprint = false;
            duplicate.ReviewedBy = adminName;
            duplicate.ReviewedUtc = now;
            var note = $"Removed duplicate reprint queue item. Active queue item #{keepItem.Id} remains for this honoree.";
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

        await db.SaveChangesAsync(ct);
    }
}
