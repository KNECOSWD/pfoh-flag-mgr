using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed record FlagGridRemovalDuplicate(
    int HonoreeId,
    string Name,
    string? FlagGridName,
    string? Rank,
    string? ServiceBranchName,
    string Reasons);

public sealed record FlagGridRemovalPreview(
    int HonoreeId,
    string HonoreeName,
    int? FlagGridId,
    string? FlagGridName,
    IReadOnlyList<FlagGridRemovalDuplicate> Duplicates);

public sealed record FlagGridRemovalResponse(
    int? FlagGridId,
    string? FlagGridName,
    int HonoreeId,
    string HonoreeName,
    int? RemovalQueueItemId,
    string? PdfWarning);

public sealed class FlagGridRemovalResult
{
    public string? Error { get; init; }

    public int Status { get; init; }

    public FlagGridRemovalPreview? Preview { get; init; }

    public FlagGridRemovalResponse? Body { get; init; }

    public static FlagGridRemovalResult Fail(int status, string message) =>
        new() { Status = status, Error = message };

    public static FlagGridRemovalResult Ok(FlagGridRemovalResponse body) =>
        new() { Status = StatusCodes.Status200OK, Body = body };
}

public sealed class FlagGridRestoreResult
{
    public string? Error { get; init; }

    public int Status { get; init; }

    public FlagGridRemovalResponse? Body { get; init; }
}

/// <summary>
/// Removes an honoree from a flag spot: frees both sides of the assignment,
/// deactivates the honoree, queues the card, closes claims and pending edits,
/// and writes an audit row. Restore puts that honoree back on the same spot
/// when the spot is still open.
/// </summary>
public sealed class FlagGridRemovalService(PfohDbContext db, HonoreePdfService pdfs)
{
    public const int ReasonMaxLength = 1000;

    public const string DuplicateCode = "possible-duplicates";

    public const string RestoreCancelReason = "Restored to the same spot";

    private static readonly string[] OpenClaimStatuses =
    [
        "Claimed",
        "Submitted",
        "Approved",
        "AdminDirectEdit",
        "AdminReprintQueued"
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<FlagGridRemovalResult> PreviewAsync(int? flagGridId, int? honoreeId, CancellationToken ct)
    {
        var target = await ResolveAsync(flagGridId, honoreeId, ct);
        if (target.Error is not null)
        {
            return FlagGridRemovalResult.Fail(target.Status, target.Error);
        }

        var preview = await BuildPreviewAsync(target.Honoree!, target.Grid!, ct);
        return new FlagGridRemovalResult
        {
            Status = StatusCodes.Status200OK,
            Preview = preview
        };
    }

    public async Task<FlagGridRemovalResult> RemoveAsync(
        int? flagGridId,
        int? honoreeId,
        string? reason,
        bool acknowledgeDuplicates,
        AdminActor actor,
        CancellationToken ct)
    {
        var why = reason?.Trim() ?? "";
        if (why.Length == 0)
        {
            return FlagGridRemovalResult.Fail(StatusCodes.Status400BadRequest, "A reason is required.");
        }

        if (why.Length > ReasonMaxLength)
        {
            return FlagGridRemovalResult.Fail(
                StatusCodes.Status400BadRequest,
                $"The reason must be {ReasonMaxLength} characters or fewer.");
        }

        var target = await ResolveAsync(flagGridId, honoreeId, ct);
        if (target.Error is not null)
        {
            return FlagGridRemovalResult.Fail(target.Status, target.Error);
        }

        var honoree = target.Honoree!;
        var grid = target.Grid!;
        var preview = await BuildPreviewAsync(honoree, grid, ct);
        if (preview.Duplicates.Count > 0 && !acknowledgeDuplicates)
        {
            return new FlagGridRemovalResult
            {
                Status = StatusCodes.Status409Conflict,
                Error = "Other active honorees have a similar name or details on a different grid.",
                Preview = preview
            };
        }

        await FlagGridRemovalAuditSchema.EnsureAsync(db, ct);
        await ReprintQueueEventSchema.EnsureAsync(db, ct);

        var now = DateTime.UtcNow;
        var claims = await db.FlagClaims
            .Where(claim => claim.HonoreeId == honoree.Id && OpenClaimStatuses.Contains(claim.ClaimStatus))
            .ToListAsync(ct);
        var pendingStatuses = new[] { "Draft", "Submitted", PendingReviewMaintenance.Withdrawal };
        var pending = await db.HonoreeChangeRequests
            .Where(request => request.HonoreeId == honoree.Id && pendingStatuses.Contains(request.RequestStatus))
            .ToListAsync(ct);
        var reprints = await db.HonoreeChangeRequests
            .Where(request =>
                request.HonoreeId == honoree.Id &&
                request.RequestStatus == "Approved" &&
                request.RequiresCardReprint &&
                request.CardPrintedUtc == null)
            .ToListAsync(ct);
        var extras = await db.Honorees
            .Where(item => item.Id != honoree.Id && item.FlagGridId == grid.Id)
            .ToListAsync(ct);

        var before = Snapshot(honoree, grid, claims, pending, reprints, extras);

        foreach (var extra in extras)
        {
            extra.FlagGridId = null;
            extra.ModifiedBy = actor.Email;
            extra.ModifiedDate = now;
        }

        FlagSpotRelease.ClearAssignment(honoree, grid, actor.Email, now);
        honoree.IsActive = false;
        honoree.DeletedDate ??= now;
        honoree.DeletedBy = string.IsNullOrWhiteSpace(honoree.DeletedBy) ? actor.Email : honoree.DeletedBy;

        foreach (var claim in claims)
        {
            claim.ClaimStatus = "Unclaimed";
            claim.FlagGridId = null;
            claim.AdminNotes = Append(claim.AdminNotes, $"Unclaimed when the honoree was removed from {grid.FlagGridName}. Reason: {why}");
        }

        foreach (var request in pending)
        {
            request.RequestStatus = "Cancelled";
            request.RequiresCardReprint = false;
            request.ReviewedBy = actor.Email;
            request.ReviewedUtc = now;
            request.ReviewNotes = Append(request.ReviewNotes, $"Cancelled because the honoree was removed from the flag grid. Reason: {why}");
        }

        foreach (var reprint in reprints)
        {
            reprint.RequiresCardReprint = false;
            reprint.ReviewedBy = actor.Email;
            reprint.ReviewedUtc = now;
            reprint.ReviewNotes = Append(
                reprint.ReviewNotes,
                $"Removed from the reprint queue by {actor.Email} at {now:u}. Reason: {why}");
            ReprintQueueAudit.Add(
                db,
                reprint.Id,
                honoree.Id,
                ReprintQueueActions.Remove,
                actor.Email,
                actor.ObjectId,
                now,
                why,
                "flag-removal",
                reprints.Count);
        }

        var queueItem = await FlagRemovalQueue.HasOpenAsync(db, honoree.Id, grid.Id, ct)
            ? await db.FlagRemovalQueueItems.FirstAsync(item =>
                item.HonoreeId == honoree.Id &&
                item.FlagGridId == grid.Id &&
                item.DoneUtc == null &&
                item.CanceledUtc == null, ct)
            : FlagRemovalQueue.Record(
                db,
                honoree,
                grid,
                FlagRemovalQueue.Manual,
                actor.Email,
                now,
                sourceDetail: why);

        var after = Snapshot(honoree, grid, claims, pending, reprints, extras);
        var audit = new FlagGridRemovalAudit
        {
            HonoreeId = honoree.Id,
            FlagGridId = grid.Id,
            Action = "Remove",
            ActorEmail = actor.Email,
            ActorObjectId = actor.ObjectId,
            OccurredUtc = now,
            Reason = why,
            BeforeState = JsonSerializer.Serialize(before, Json),
            AfterState = JsonSerializer.Serialize(after, Json)
        };
        db.FlagGridRemovalAudits.Add(audit);
        await db.SaveChangesAsync(ct);
        audit.RemovalQueueItemId = queueItem.Id;
        await db.SaveChangesAsync(ct);
        await HonoreeClaimStore.SyncAsync(db, ct);

        var warning = await pdfs.TryRebuildAsync(honoree.Id, ct, includeInactive: true);
        if (warning is not null)
        {
            audit.PdfWarning = Clip(warning, 500);
            await db.SaveChangesAsync(ct);
            warning = "The honoree was removed from the flag grid, but the card PDF could not be rebuilt. The removal was saved.";
        }

        return FlagGridRemovalResult.Ok(new FlagGridRemovalResponse(
            grid.Id,
            grid.FlagGridName,
            honoree.Id,
            FlagRemovalQueue.DisplayName(honoree),
            queueItem.Id,
            warning));
    }

    public async Task<FlagGridRestoreResult> RestoreAsync(int queueItemId, AdminActor actor, CancellationToken ct)
    {
        await FlagGridRemovalAuditSchema.EnsureAsync(db, ct);
        var item = await db.FlagRemovalQueueItems.FirstOrDefaultAsync(row => row.Id == queueItemId, ct);
        if (item is null)
        {
            return new FlagGridRestoreResult
            {
                Status = StatusCodes.Status404NotFound,
                Error = "Removal queue row was not found."
            };
        }

        if (item.DoneUtc != null || item.CanceledUtc != null)
        {
            return new FlagGridRestoreResult
            {
                Status = StatusCodes.Status409Conflict,
                Error = "This removal queue row is already finished."
            };
        }

        if (item.HonoreeId is not int honoreeId)
        {
            return new FlagGridRestoreResult
            {
                Status = StatusCodes.Status409Conflict,
                Error = "This removal queue row has no honoree to restore."
            };
        }

        var honoree = await db.Honorees.FirstOrDefaultAsync(row => row.Id == honoreeId, ct);
        var grid = await db.FlagGrids.FirstOrDefaultAsync(row => row.Id == item.FlagGridId, ct);
        if (honoree is null || grid is null)
        {
            return new FlagGridRestoreResult
            {
                Status = StatusCodes.Status404NotFound,
                Error = "The honoree or flag spot for this row was not found."
            };
        }

        if (grid.DeletedDate != null)
        {
            return new FlagGridRestoreResult
            {
                Status = StatusCodes.Status409Conflict,
                Error = $"Spot {grid.FlagGridName} is no longer on the field. Restore is blocked."
            };
        }

        var blocker = await db.Honorees.FirstOrDefaultAsync(row =>
            row.Id != honoree.Id &&
            row.IsActive &&
            row.DeletedDate == null &&
            (row.FlagGridId == grid.Id || row.Id == grid.HonoreeId), ct);
        if (blocker is null && grid.HonoreeId is int occupant && occupant != honoree.Id)
        {
            blocker = await db.Honorees.FirstOrDefaultAsync(row =>
                row.Id == occupant && row.IsActive && row.DeletedDate == null, ct);
        }

        if (blocker is not null)
        {
            return new FlagGridRestoreResult
            {
                Status = StatusCodes.Status409Conflict,
                Error = $"Spot {grid.FlagGridName} is now assigned to {FlagRemovalQueue.DisplayName(blocker)}. Restore is blocked until that spot is open."
            };
        }

        var now = DateTime.UtcNow;
        var before = new
        {
            honoree = new { honoree.Id, honoree.IsActive, honoree.FlagGridId, honoree.DeletedDate, honoree.DeletedBy },
            flagGrid = new { grid.Id, grid.HonoreeId },
            queue = new { item.Id, item.CanceledUtc, item.CancelReason }
        };

        var stray = await db.Honorees
            .Where(row => row.Id != honoree.Id && row.FlagGridId == grid.Id)
            .ToListAsync(ct);
        foreach (var other in stray)
        {
            other.FlagGridId = null;
            other.ModifiedBy = actor.Email;
            other.ModifiedDate = now;
        }

        honoree.IsActive = true;
        honoree.DeletedDate = null;
        honoree.DeletedBy = null;
        honoree.FlagGridId = grid.Id;
        honoree.ModifiedBy = actor.Email;
        honoree.ModifiedDate = now;
        grid.HonoreeId = honoree.Id;
        grid.ModifiedBy = actor.Email;
        grid.ModifiedDate = now;
        item.CanceledUtc = now;
        item.CanceledBy = actor.Email;
        item.CancelReason = RestoreCancelReason;

        var after = new
        {
            honoree = new { honoree.Id, honoree.IsActive, honoree.FlagGridId, honoree.DeletedDate, honoree.DeletedBy },
            flagGrid = new { grid.Id, grid.HonoreeId },
            queue = new { item.Id, item.CanceledUtc, item.CancelReason }
        };
        var audit = new FlagGridRemovalAudit
        {
            HonoreeId = honoree.Id,
            FlagGridId = grid.Id,
            RemovalQueueItemId = item.Id,
            Action = "Restore",
            ActorEmail = actor.Email,
            ActorObjectId = actor.ObjectId,
            OccurredUtc = now,
            Reason = RestoreCancelReason,
            BeforeState = JsonSerializer.Serialize(before, Json),
            AfterState = JsonSerializer.Serialize(after, Json)
        };
        db.FlagGridRemovalAudits.Add(audit);
        await db.SaveChangesAsync(ct);

        var warning = await pdfs.TryRebuildAsync(honoree.Id, ct);
        if (warning is not null)
        {
            audit.PdfWarning = Clip(warning, 500);
            await db.SaveChangesAsync(ct);
            warning = "The honoree was restored to the spot, but the card PDF could not be rebuilt. Opening the PDF will try again.";
        }

        return new FlagGridRestoreResult
        {
            Status = StatusCodes.Status200OK,
            Body = new FlagGridRemovalResponse(
                grid.Id,
                grid.FlagGridName,
                honoree.Id,
                FlagRemovalQueue.DisplayName(honoree),
                item.Id,
                warning)
        };
    }

    private async Task<FlagGridRemovalPreview> BuildPreviewAsync(Honoree honoree, FlagGrid grid, CancellationToken ct)
    {
        var active = await DuplicateReviewGroups.LoadActiveAsync(db, ct);
        var suppressed = await DuplicateReviewGroups.LoadSuppressedAsync(db, ct);
        var groups = DuplicateReviewGroups.Match(active, suppressed);
        var byId = active.ToDictionary(item => item.Id);
        var duplicates = new List<FlagGridRemovalDuplicate>();
        foreach (var group in groups)
        {
            if (!group.Ids.Contains(honoree.Id))
            {
                continue;
            }

            foreach (var pair in group.Pairs)
            {
                var otherId = pair.LeftId == honoree.Id ? pair.RightId : pair.RightId == honoree.Id ? pair.LeftId : 0;
                if (otherId == 0 || !byId.TryGetValue(otherId, out var other))
                {
                    continue;
                }

                if (other.FlagGridId is null || other.FlagGridId == grid.Id)
                {
                    continue;
                }

                if (duplicates.Any(item => item.HonoreeId == other.Id))
                {
                    continue;
                }

                duplicates.Add(new FlagGridRemovalDuplicate(
                    other.Id,
                    DuplicateReviewGroups.DisplayName(other),
                    other.FlagGrid?.FlagGridName,
                    string.IsNullOrWhiteSpace(other.Rank) ? null : other.Rank,
                    other.ServiceBranch?.ServiceBranchName,
                    pair.Explanation));
            }
        }

        duplicates.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return new FlagGridRemovalPreview(
            honoree.Id,
            FlagRemovalQueue.DisplayName(honoree),
            grid.Id,
            grid.FlagGridName,
            duplicates);
    }

    private async Task<(Honoree? Honoree, FlagGrid? Grid, string? Error, int Status)> ResolveAsync(
        int? flagGridId,
        int? honoreeId,
        CancellationToken ct)
    {
        if (honoreeId is int requestedHonoree)
        {
            var honoree = await db.Honorees
                .Include(item => item.FlagGrid)
                .Include(item => item.ServiceBranch)
                .FirstOrDefaultAsync(item => item.Id == requestedHonoree && item.IsActive && item.DeletedDate == null, ct);
            if (honoree is null)
            {
                return (null, null, "Honoree was not found.", StatusCodes.Status404NotFound);
            }

            FlagGrid? grid = null;
            if (honoree.FlagGridId is int linked)
            {
                grid = honoree.FlagGrid ?? await db.FlagGrids.FirstOrDefaultAsync(item => item.Id == linked && item.DeletedDate == null, ct);
            }

            grid ??= await db.FlagGrids.FirstOrDefaultAsync(item =>
                item.HonoreeId == honoree.Id && item.DeletedDate == null, ct);
            if (grid is null)
            {
                return (null, null, "This honoree is not on a flag grid.", StatusCodes.Status400BadRequest);
            }

            return (honoree, grid, null, StatusCodes.Status200OK);
        }

        if (flagGridId is not int gridId)
        {
            return (null, null, "A flag grid or honoree is required.", StatusCodes.Status400BadRequest);
        }

        var spot = await db.FlagGrids.FirstOrDefaultAsync(item => item.Id == gridId && item.DeletedDate == null, ct);
        if (spot is null)
        {
            return (null, null, "Flag position was not found.", StatusCodes.Status404NotFound);
        }

        var occupant = await db.Honorees
            .Include(item => item.ServiceBranch)
            .FirstOrDefaultAsync(item =>
                item.FlagGridId == spot.Id &&
                item.IsActive &&
                item.DeletedDate == null, ct);
        if (occupant is null && spot.HonoreeId is int linkedHonoree)
        {
            occupant = await db.Honorees
                .Include(item => item.ServiceBranch)
                .FirstOrDefaultAsync(item =>
                    item.Id == linkedHonoree &&
                    item.IsActive &&
                    item.DeletedDate == null, ct);
        }

        if (occupant is null)
        {
            return (null, null, "No active honoree is assigned to this flag grid.", StatusCodes.Status404NotFound);
        }

        return (occupant, spot, null, StatusCodes.Status200OK);
    }

    private static object Snapshot(
        Honoree honoree,
        FlagGrid grid,
        IReadOnlyList<FlagClaim> claims,
        IReadOnlyList<HonoreeChangeRequest> pending,
        IReadOnlyList<HonoreeChangeRequest> reprints,
        IReadOnlyList<Honoree> extras) => new
    {
        honoree = new
        {
            honoree.Id,
            honoree.IsActive,
            honoree.FlagGridId,
            honoree.DeletedDate,
            honoree.DeletedBy
        },
        flagGrid = new { grid.Id, grid.HonoreeId },
        claims = claims.Select(claim => new { claim.Id, claim.ClaimStatus, claim.FlagGridId }).ToList(),
        changeRequests = pending.Select(request => new { request.Id, request.RequestStatus, request.RequiresCardReprint }).ToList(),
        reprintQueueItemIds = reprints.Select(request => request.Id).ToList(),
        otherFlagGridPointers = extras.Select(item => new { item.Id, item.FlagGridId }).ToList()
    };

    private static string Append(string? existing, string note) =>
        string.IsNullOrWhiteSpace(existing) ? note : $"{existing}\n{note}";

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
