using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public class PendingReviewMaintenance(PfohDbContext db)
{
    public const string Withdrawn = "Withdrawn";
    public const string Superseded = "Superseded";
    public const string Withdrawal = "Withdrawal";
    public const string Removed = "Removed";

    public readonly record struct CardRef(
        int Id,
        int? FlagGridId,
        int? HonoreeId,
        DateTime? SubmittedUtc,
        DateTime CreatedUtc,
        string? OwnerObjectId);

    public static bool SameCard(CardRef left, CardRef right)
    {
        var sameSpot = left.FlagGridId is > 0 && left.FlagGridId == right.FlagGridId
            || left.HonoreeId is > 0 && left.HonoreeId == right.HonoreeId;
        if (!sameSpot)
        {
            return false;
        }

        // A pre-existing row with no account still groups with another row that
        // also has no account. A signed-in account never hides or replaces a
        // different account.
        var leftOwner = string.IsNullOrWhiteSpace(left.OwnerObjectId) ? null : left.OwnerObjectId.Trim();
        var rightOwner = string.IsNullOrWhiteSpace(right.OwnerObjectId) ? null : right.OwnerObjectId.Trim();
        if (leftOwner is null && rightOwner is null)
        {
            return true;
        }

        return leftOwner is not null && string.Equals(leftOwner, rightOwner, StringComparison.Ordinal);
    }

    public static IReadOnlyList<T> KeepNewest<T>(IReadOnlyList<T> items, Func<T, CardRef> card)
    {
        var keepIds = new HashSet<int>();
        var kept = new List<CardRef>();
        foreach (var item in items
            .OrderByDescending(item => card(item).SubmittedUtc ?? card(item).CreatedUtc)
            .ThenByDescending(item => card(item).Id))
        {
            var current = card(item);
            if (kept.Any(existing => SameCard(existing, current)))
            {
                continue;
            }

            kept.Add(current);
            keepIds.Add(current.Id);
        }

        return items.Where(item => keepIds.Contains(card(item).Id)).ToList();
    }

    public async Task SupersedeOlderPendingAsync(HonoreeChangeRequest keeper, CancellationToken ct)
    {
        var keeperOwner = keeper.FlagClaim?.ExternalUserObjectId;
        if (string.IsNullOrWhiteSpace(keeperOwner) && keeper.FlagClaimId > 0)
        {
            keeperOwner = await db.FlagClaims
                .Where(claim => claim.Id == keeper.FlagClaimId)
                .Select(claim => claim.ExternalUserObjectId)
                .FirstOrDefaultAsync(ct);
        }

        var keeperCard = new CardRef(
            keeper.Id,
            keeper.FlagGridId,
            keeper.HonoreeId,
            keeper.SubmittedUtc,
            keeper.CreatedUtc,
            keeperOwner);

        var pending = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .Where(request => request.RequestStatus == "Submitted" && request.Id != keeper.Id)
            .ToListAsync(ct);

        var older = pending
            .Where(request => SameCard(
                keeperCard,
                new CardRef(
                    request.Id,
                    request.FlagGridId,
                    request.HonoreeId,
                    request.SubmittedUtc,
                    request.CreatedUtc,
                    request.FlagClaim?.ExternalUserObjectId)))
            .ToList();

        if (older.Count == 0)
        {
            return;
        }

        foreach (var request in older)
        {
            await MarkSupersededAsync(request, keeper, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task CleanupDuplicatePendingAsync(CancellationToken ct)
    {
        var pending = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .Where(request => request.RequestStatus == "Submitted")
            .ToListAsync(ct);

        var keepers = KeepNewest(
            pending,
            request => new CardRef(
                request.Id,
                request.FlagGridId,
                request.HonoreeId,
                request.SubmittedUtc,
                request.CreatedUtc,
                request.FlagClaim?.ExternalUserObjectId));
        var keepIds = keepers.Select(request => request.Id).ToHashSet();

        foreach (var request in pending.Where(request => !keepIds.Contains(request.Id)))
        {
            var keeper = keepers.First(candidate => SameCard(
                new CardRef(
                    candidate.Id,
                    candidate.FlagGridId,
                    candidate.HonoreeId,
                    candidate.SubmittedUtc,
                    candidate.CreatedUtc,
                    candidate.FlagClaim?.ExternalUserObjectId),
                new CardRef(
                    request.Id,
                    request.FlagGridId,
                    request.HonoreeId,
                    request.SubmittedUtc,
                    request.CreatedUtc,
                    request.FlagClaim?.ExternalUserObjectId)));
            await MarkSupersededAsync(request, keeper, ct);
        }

        if (pending.Any(request => !keepIds.Contains(request.Id)))
        {
            await db.SaveChangesAsync(ct);
        }

        await ReleasePendingSubmissionSpotsAsync(ct);
    }

    public async Task ReleasePendingSubmissionSpotsAsync(CancellationToken ct)
    {
        var held = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .Where(request =>
                (request.HonoreeId == null || request.HonoreeId <= 0) &&
                (request.RequestStatus == "Submitted" ||
                 request.RequestStatus == Superseded ||
                 request.RequestStatus == "Rejected" ||
                 request.RequestStatus == "Draft") &&
                (request.FlagGridId != null ||
                 (request.FlagClaim != null &&
                  request.FlagClaim.HonoreeId == null &&
                  request.FlagClaim.FlagGridId != null)))
            .ToListAsync(ct);

        if (held.Count == 0)
        {
            return;
        }

        foreach (var request in held)
        {
            request.FlagGridId = null;
            if (request.FlagClaim is { HonoreeId: null })
            {
                request.FlagClaim.FlagGridId = null;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task MarkSupersededAsync(HonoreeChangeRequest request, HonoreeChangeRequest keeper, CancellationToken ct)
    {
        if (keeper.FlagClaim is null && keeper.FlagClaimId > 0)
        {
            await db.Entry(keeper).Reference(row => row.FlagClaim).LoadAsync(ct);
        }

        request.RequestStatus = Superseded;
        var note = $"Superseded by a newer submission for this honoree. Change request {keeper.Id} remains in review.";
        request.ReviewNotes = string.IsNullOrWhiteSpace(request.ReviewNotes)
            ? note
            : request.ReviewNotes.Contains(note, StringComparison.Ordinal)
                ? request.ReviewNotes
                : $"{request.ReviewNotes.Trim()}\n{note}";

        var actorEmail = keeper.FlagClaim?.ExternalUserEmail;
        db.SubmissionAuditEvents.Add(new SubmissionAuditEvent
        {
            Action = SubmissionAuditEvent.Superseded,
            ChangeRequestId = request.Id,
            KeeperChangeRequestId = keeper.Id,
            ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? "signed-in user" : actorEmail.Trim(),
            ActorObjectId = keeper.FlagClaim?.ExternalUserObjectId,
            CreatedUtc = DateTime.UtcNow,
            Note = note
        });
    }
}
