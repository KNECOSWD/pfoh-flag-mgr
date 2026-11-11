using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

/// <summary>
/// Lists inactive honorees and restores them. A former flag that is reserved,
/// gone, or held by someone else is not reused. The administrator must send an
/// open flag. An honoree with no flag on record can be active without one,
/// which is already allowed for an approved honoree who has not been placed.
/// A merged record stays inactive; merge history undo is the way back.
/// Each restore is one serializable transaction. On SQL Server the flag row is
/// claimed with a conditional update, and the filtered unique index rejects a
/// second active assignment. No email is sent.
/// </summary>
public sealed class InactiveHonoreeAdminService(
    PfohDbContext db,
    HonoreePdfService pdfs,
    ReprintQueueMaintenance reprints,
    InactiveRestoreGate restoreGate,
    ILogger<InactiveHonoreeAdminService> logger)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 50;
    public const int MaxBatch = 50;
    public const int MaxFreeSlots = 100;
    public const string NotRecorded = "Not recorded";

    private const string RestoreAction = "Restore";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<InactiveHonoreeListDto> ListAsync(string? query, int page, int pageSize, CancellationToken ct)
    {
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        var needle = NormalizeQuery(query);
        var honorees = await db.Honorees
            .AsNoTracking()
            .Include(honoree => honoree.ServiceBranch)
            .Where(honoree => !honoree.IsActive)
            .ToListAsync(ct);
        var facts = await LoadFactsAsync(honorees, ct);
        var rows = honorees
            .Select(honoree => (Honoree: honoree, Dto: ToDto(honoree, facts[honoree.Id])))
            .Where(row => Matches(needle, row.Dto))
            .OrderBy(row => row.Honoree.LastName)
            .ThenBy(row => row.Honoree.FirstName)
            .ThenBy(row => row.Honoree.Id)
            .Select(row => row.Dto)
            .ToList();
        var total = rows.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = page < 1 ? 1 : Math.Min(page, pageCount);
        var items = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new InactiveHonoreeListDto(items, page, pageSize, total);
    }

    public async Task<InactiveHonoreeFreeSlotListDto> ListFreeSlotsAsync(string? query, CancellationToken ct)
    {
        var needle = NormalizeQuery(query);
        var grids = await db.FlagGrids.AsNoTracking().Where(grid => grid.DeletedDate == null).ToListAsync(ct);
        var occupants = await LoadOccupantsAsync(ct);
        var open = grids
            .Where(grid => !grid.Reserved && OccupantOf(grid, occupants, exceptHonoreeId: 0) is null)
            .Where(grid => needle.Length == 0 || FlagPositionLabel.Matches(needle, grid.FlagGridName))
            .OrderBy(grid => grid, Comparer<FlagGrid>.Create((left, right) =>
                FlagRemovalQueue.CompareSpots(left.FlagGridName, right.FlagGridName)))
            .Select(grid => new InactiveHonoreeFreeSlotDto(grid.Id, grid.FlagGridName))
            .ToList();
        var truncated = open.Count > MaxFreeSlots;
        return new InactiveHonoreeFreeSlotListDto(
            truncated ? open.Take(MaxFreeSlots).ToList() : open,
            truncated);
    }

    public async Task<InactiveHonoreeReactivateResultDto> ReactivateAsync(
        IReadOnlyList<InactiveHonoreeReactivateItem> items,
        AdminActor actor,
        CancellationToken ct)
    {
        var results = new List<InactiveHonoreeReactivateItemResultDto>(items.Count);
        foreach (var item in items)
        {
            results.Add(await ReactivateOneAsync(item, actor, ct));
        }

        return new InactiveHonoreeReactivateResultDto(results);
    }

    private async Task<InactiveHonoreeReactivateItemResultDto> ReactivateOneAsync(
        InactiveHonoreeReactivateItem item,
        AdminActor actor,
        CancellationToken ct)
    {
        InactiveHonoreeReactivateItemResultDto result;
        if (restoreGate.BeforeRestore is not null)
        {
            await restoreGate.BeforeRestore(ct);
        }

        if (!db.Database.IsRelational())
        {
            result = await RestoreCoreAsync(item, actor, transaction: null, ct);
        }
        else
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                result = await RestoreCoreAsync(item, actor, transaction, ct);
                if (result.Outcome == "reactivated")
                {
                    await transaction.CommitAsync(ct);
                }
                else
                {
                    await transaction.RollbackAsync(ct);
                }
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        db.ChangeTracker.Clear();
        if (result.Outcome != "reactivated")
        {
            return result;
        }

        var warning = await RebuildPdfAsync(result.HonoreeId, ct);
        return warning is null ? result : result with { PdfWarning = warning };
    }

    private async Task<InactiveHonoreeReactivateItemResultDto> RestoreCoreAsync(
        InactiveHonoreeReactivateItem item,
        AdminActor actor,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken ct)
    {
        try
        {
            await FlagGridRemovalAuditSchema.EnsureAsync(db, ct);
            var honoree = await db.Honorees
                .Include(row => row.ServiceBranch)
                .FirstOrDefaultAsync(row => row.Id == item.HonoreeId, ct);
            if (honoree is null)
            {
                return Result(item.HonoreeId, $"Honoree {item.HonoreeId}", "not-found", "not-found", "That honoree was not found.");
            }

            var name = FlagRemovalQueue.DisplayName(honoree);
            if (honoree.IsActive)
            {
                return Result(honoree.Id, name, "blocked", "already-active", $"{name} was not restored. This honoree is already active.");
            }

            var facts = (await LoadFactsAsync([honoree], ct))[honoree.Id];
            if (!facts.CanReactivate)
            {
                return Result(
                    honoree.Id,
                    name,
                    "blocked",
                    "merged",
                    $"{name} was not restored. {facts.Reason} Undo that merge from Merge history.");
            }

            var requested = item.FlagGridId is > 0 ? item.FlagGridId : null;
            int? targetId = requested ?? (facts.SlotStatus == "free" ? facts.FlagGridId : null);
            if (targetId is null)
            {
                if (facts.SlotStatus == "none")
                {
                    return await FinishWithoutFlagAsync(honoree, name, actor, ct);
                }

                return Result(
                    honoree.Id,
                    name,
                    "blocked",
                    facts.SlotStatus == "reserved" ? "reserved" : facts.SlotStatus == "missing" ? "missing" : "slot-conflict",
                    Blocked(name, facts));
            }

            var grid = await db.FlagGrids.FirstOrDefaultAsync(row => row.Id == targetId.Value, ct);
            if (grid is null || grid.DeletedDate != null)
            {
                var label = grid?.FlagGridName ?? facts.FlagName ?? "That flag";
                return Result(honoree.Id, name, "blocked", "missing", $"{name} was not restored. Flag {label} is no longer on the field. Choose an open flag.");
            }

            if (grid.Reserved)
            {
                return Result(honoree.Id, name, "blocked", "reserved", $"{name} was not restored. Flag {grid.FlagGridName} is reserved. Choose an open flag.");
            }

            var occupants = await LoadOccupantsAsync(ct);
            var occupant = OccupantOf(grid, occupants, honoree.Id);
            if (occupant is not null)
            {
                return Result(
                    honoree.Id,
                    name,
                    "blocked",
                    "slot-conflict",
                    $"{name} was not restored. Flag {grid.FlagGridName} is assigned to {occupant.Name}. Choose an open flag.");
            }

            if (db.Database.IsRelational())
            {
                var claimed = await ClaimFlagRowAsync(grid.Id, honoree.Id, actor.Email, ct);
                if (claimed != 1)
                {
                    return Result(
                        honoree.Id,
                        name,
                        "blocked",
                        "slot-conflict",
                        $"{name} was not restored. Flag {grid.FlagGridName} was just assigned to someone else. Choose an open flag.");
                }
            }

            var now = DateTime.UtcNow;
            var strays = await db.Honorees
                .Where(row => row.Id != honoree.Id && !row.IsActive && row.FlagGridId == grid.Id)
                .ToListAsync(ct);
            foreach (var stray in strays)
            {
                stray.FlagGridId = null;
                stray.ModifiedBy = actor.Email;
                stray.ModifiedDate = now;
            }

            var previousDeleted = honoree.DeletedDate;
            var previousDeletedBy = honoree.DeletedBy;
            var previousFlag = honoree.FlagGridId;
            var previousGridHonoree = grid.HonoreeId;
            var sameSpot = facts.FlagGridId == grid.Id;
            var openRemoval = await db.FlagRemovalQueueItems
                .Where(row =>
                    row.HonoreeId == honoree.Id &&
                    row.FlagGridId == grid.Id &&
                    row.DoneUtc == null &&
                    row.CanceledUtc == null)
                .ToListAsync(ct);

            var before = Snapshot(honoree, grid);
            honoree.IsActive = true;
            honoree.DeletedDate = null;
            honoree.DeletedBy = null;
            honoree.FlagGridId = grid.Id;
            honoree.ModifiedBy = actor.Email;
            honoree.ModifiedDate = now;
            grid.HonoreeId = honoree.Id;
            grid.ModifiedBy = actor.Email;
            grid.ModifiedDate = now;

            await FlagRemovalQueue.NoteAssignmentAsync(db, honoree, grid, actor.Email, now, ct);
            if (sameSpot)
            {
                var stillOpen = await db.FlagRemovalQueueItems
                    .Where(row =>
                        row.HonoreeId == honoree.Id &&
                        row.FlagGridId == grid.Id &&
                        row.DoneUtc == null &&
                        row.CanceledUtc == null)
                    .ToListAsync(ct);
                foreach (var row in stillOpen)
                {
                    row.CanceledUtc = now;
                    row.CanceledBy = actor.Email;
                    row.CancelReason = FlagGridRemovalService.RestoreCancelReason;
                }
            }

            var removalCanceled = sameSpot && openRemoval.Count > 0;
            var after = Snapshot(honoree, grid);
            var audit = new FlagGridRemovalAudit
            {
                HonoreeId = honoree.Id,
                FlagGridId = grid.Id,
                RemovalQueueItemId = openRemoval.FirstOrDefault()?.Id,
                Action = RestoreAction,
                ActorEmail = actor.Email,
                ActorObjectId = actor.ObjectId,
                OccurredUtc = now,
                Reason = AuditReason(grid.FlagGridName, facts.FlagName, sameSpot),
                BeforeState = JsonSerializer.Serialize(before, Json),
                AfterState = JsonSerializer.Serialize(after, Json)
            };
            db.FlagGridRemovalAudits.Add(audit);
            await db.SaveChangesAsync(ct);

            var sharing = await db.Honorees.CountAsync(row =>
                row.IsActive &&
                row.DeletedDate == null &&
                row.FlagGridId == grid.Id, ct);
            if (sharing != 1)
            {
                if (transaction is null)
                {
                    honoree.IsActive = false;
                    honoree.DeletedDate = previousDeleted;
                    honoree.DeletedBy = previousDeletedBy;
                    honoree.FlagGridId = previousFlag;
                    grid.HonoreeId = previousGridHonoree;
                    db.FlagGridRemovalAudits.Remove(audit);
                    await db.SaveChangesAsync(ct);
                }

                return Result(
                    honoree.Id,
                    name,
                    "blocked",
                    "slot-conflict",
                    $"{name} was not restored. Flag {grid.FlagGridName} was just assigned to someone else. Choose an open flag.");
            }

            var reprintQueued = false;
            if (!removalCanceled)
            {
                reprintQueued = await EnqueueReprintAsync(honoree, grid, actor, ct);
            }

            var message = $"{name} is active on flag {grid.FlagGridName}.";
            if (removalCanceled)
            {
                message += " The open removal for this flag was canceled.";
            }
            else if (reprintQueued)
            {
                message += " The honor card is in the print queue.";
            }

            return Result(
                honoree.Id,
                name,
                "reactivated",
                "ok",
                message,
                grid.Id,
                grid.FlagGridName,
                reprintQueued,
                removalCanceled,
                null);
        }
        catch (DbUpdateException ex) when (IsSlotConflict(ex))
        {
            logger.LogWarning(ex, "Flag slot conflict while restoring honoree {HonoreeId}.", item.HonoreeId);
            return Result(
                item.HonoreeId,
                $"Honoree {item.HonoreeId}",
                "blocked",
                "slot-conflict",
                $"Honoree {item.HonoreeId} was not restored. That flag was just assigned to someone else. Choose an open flag.");
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Honoree {HonoreeId} could not be restored.", item.HonoreeId);
            return Result(
                item.HonoreeId,
                $"Honoree {item.HonoreeId}",
                "blocked",
                "save-failed",
                $"Honoree {item.HonoreeId} was not restored because the save failed.");
        }
    }

    private async Task<InactiveHonoreeReactivateItemResultDto> FinishWithoutFlagAsync(
        Honoree honoree,
        string name,
        AdminActor actor,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var before = Snapshot(honoree, null);
        honoree.IsActive = true;
        honoree.DeletedDate = null;
        honoree.DeletedBy = null;
        honoree.FlagGridId = null;
        honoree.ModifiedBy = actor.Email;
        honoree.ModifiedDate = now;
        var after = Snapshot(honoree, null);
        var audit = new FlagGridRemovalAudit
        {
            HonoreeId = honoree.Id,
            FlagGridId = null,
            Action = RestoreAction,
            ActorEmail = actor.Email,
            ActorObjectId = actor.ObjectId,
            OccurredUtc = now,
            Reason = "Reactivated by an administrator. No flag position was on record.",
            BeforeState = JsonSerializer.Serialize(before, Json),
            AfterState = JsonSerializer.Serialize(after, Json)
        };
        db.FlagGridRemovalAudits.Add(audit);
        await db.SaveChangesAsync(ct);
        return Result(
            honoree.Id,
            name,
            "reactivated",
            "no-slot",
            $"{name} is active with no flag position.",
            null,
            null,
            false,
            false,
            null);
    }

    private async Task<int> ClaimFlagRowAsync(int flagGridId, int honoreeId, string actorEmail, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.FlagGrids
            .Where(grid =>
                grid.Id == flagGridId &&
                grid.DeletedDate == null &&
                !grid.Reserved &&
                !db.Honorees.Any(honoree =>
                    honoree.Id != honoreeId &&
                    honoree.IsActive &&
                    honoree.DeletedDate == null &&
                    (honoree.FlagGridId == grid.Id || honoree.Id == grid.HonoreeId)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(grid => grid.HonoreeId, honoreeId)
                .SetProperty(grid => grid.ModifiedBy, actorEmail)
                .SetProperty(grid => grid.ModifiedDate, now), ct);
    }

    private async Task<bool> EnqueueReprintAsync(Honoree honoree, FlagGrid grid, AdminActor actor, CancellationToken ct)
    {
        const string note = "Added to the print queue because this honoree was restored to a flag.";
        await reprints.DeactivateOtherActiveReprintItemsForHonoreeAsync(
            honoree.Id, keepChangeRequestId: null, actor.Email, ct, actor.ObjectId);
        var existing = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .Where(request =>
                request.HonoreeId == honoree.Id &&
                request.RequestStatus == "Approved" &&
                request.RequiresCardReprint &&
                request.CardPrintedUtc == null)
            .OrderByDescending(request => request.ReviewedUtc ?? request.SubmittedUtc ?? request.CreatedUtc)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            existing.FlagGridId = grid.Id;
            if (existing.FlagClaim is not null)
            {
                existing.FlagClaim.FlagGridId = grid.Id;
            }

            await db.SaveChangesAsync(ct);
            return true;
        }

        var now = DateTime.UtcNow;
        var claim = new FlagClaim
        {
            FlagGridId = grid.Id,
            HonoreeId = honoree.Id,
            ExternalUserObjectId = $"admin-reprint:{Guid.NewGuid():N}",
            ExternalUserEmail = actor.Email,
            ExternalUserName = "PFOH Admin reprint queue",
            ClaimStatus = "AdminReprintQueued",
            CreatedUtc = now,
            ApprovedUtc = now,
            ApprovedBy = actor.Email,
            AdminNotes = note
        };
        var request = new HonoreeChangeRequest
        {
            FlagClaim = claim,
            FlagGridId = grid.Id,
            HonoreeId = honoree.Id,
            FirstName = honoree.FirstName,
            MiddleName = honoree.MiddleName,
            LastName = honoree.LastName,
            Suffix = honoree.Suffix,
            Nickname = honoree.Nickname,
            Rank = honoree.Rank,
            ServiceBranchId = honoree.ServiceBranchId,
            ServiceBranchCategoryId = honoree.ServiceBranchCategoryId,
            StartYear = honoree.StartYear,
            EndYear = honoree.EndYear,
            DatesUserEntry = honoree.DatesUserEntry,
            ConflictsServed = honoree.ConflictsServed,
            Awards = honoree.Awards,
            Description = honoree.Description,
            KIA = honoree.KIA,
            PhotoFileName = honoree.PhotoFileName,
            SubmitterPhoneNumber = honoree.PhoneNumber,
            SubmitterEmailAddress = honoree.EmailAddress,
            RequestStatus = "Approved",
            CreatedUtc = now,
            SubmittedUtc = now,
            ReviewedUtc = now,
            ReviewedBy = actor.Email,
            ReviewNotes = note,
            RequiresCardReprint = true
        };
        db.FlagClaims.Add(claim);
        db.HonoreeChangeRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await ReprintQueueEventSchema.EnsureAsync(db, ct);
        ReprintQueueAudit.Add(
            db,
            request.Id,
            honoree.Id,
            ReprintQueueActions.Add,
            actor.Email,
            actor.ObjectId,
            now,
            note,
            "reactivate",
            1);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<string?> RebuildPdfAsync(int honoreeId, CancellationToken ct)
    {
        try
        {
            var warning = await pdfs.TryRebuildAsync(honoreeId, ct);
            if (string.IsNullOrWhiteSpace(warning))
            {
                return null;
            }

            var audit = await db.FlagGridRemovalAudits
                .Where(row => row.HonoreeId == honoreeId && row.Action == RestoreAction)
                .OrderByDescending(row => row.OccurredUtc)
                .ThenByDescending(row => row.Id)
                .FirstOrDefaultAsync(ct);
            if (audit is not null)
            {
                audit.PdfWarning = Clip(warning, 500);
                await db.SaveChangesAsync(ct);
            }

            return "The honoree was restored, but the card PDF could not be rebuilt.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Card PDF was not rebuilt after restoring honoree {HonoreeId}.", honoreeId);
            db.ChangeTracker.Clear();
            return "The honoree was restored, but the card PDF could not be rebuilt.";
        }
    }

    private async Task<Dictionary<int, DeactivationFacts>> LoadFactsAsync(IReadOnlyList<Honoree> honorees, CancellationToken ct)
    {
        var ids = honorees.Select(honoree => honoree.Id).ToList();
        var audits = ids.Count == 0
            ? []
            : await db.FlagGridRemovalAudits.AsNoTracking()
                .Where(audit => audit.Action == "Remove" && ids.Contains(audit.HonoreeId))
                .ToListAsync(ct);
        var queues = ids.Count == 0
            ? []
            : await db.FlagRemovalQueueItems.AsNoTracking()
                .Where(item => item.HonoreeId != null && ids.Contains(item.HonoreeId.Value))
                .ToListAsync(ct);
        var merges = await db.HonoreeMergeAudits.AsNoTracking()
            .Where(audit => audit.UndoneUtc == null)
            .Select(audit => new MergeRow(audit.SurvivorId, audit.AbsorbedHonoreeIds, audit.MergedUtc, audit.MergedBy))
            .ToListAsync(ct);
        var absorbed = new Dictionary<int, MergeHit>();
        var survivorIds = new HashSet<int>();
        foreach (var merge in merges)
        {
            foreach (var absorbedId in ParseIds(merge.AbsorbedHonoreeIds))
            {
                if (!absorbed.TryGetValue(absorbedId, out var current) || merge.MergedUtc >= current.When)
                {
                    absorbed[absorbedId] = new MergeHit(merge.SurvivorId, merge.MergedUtc, merge.MergedBy);
                    survivorIds.Add(merge.SurvivorId);
                }
            }
        }

        var survivors = survivorIds.Count == 0
            ? new Dictionary<int, Honoree>()
            : await db.Honorees.AsNoTracking()
                .Where(honoree => survivorIds.Contains(honoree.Id))
                .ToDictionaryAsync(honoree => honoree.Id, ct);
        var flagIds = audits.Select(audit => audit.FlagGridId)
            .Concat(queues.Select(item => (int?)item.FlagGridId))
            .Concat(honorees.Select(honoree => honoree.FlagGridId))
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var grids = flagIds.Count == 0
            ? new Dictionary<int, FlagGrid>()
            : await db.FlagGrids.AsNoTracking()
                .Where(grid => flagIds.Contains(grid.Id))
                .ToDictionaryAsync(grid => grid.Id, ct);
        var occupants = await LoadOccupantsAsync(ct);
        var latestAudit = audits
            .GroupBy(audit => audit.HonoreeId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(audit => audit.OccurredUtc).ThenByDescending(audit => audit.Id).First());
        var latestQueue = queues
            .GroupBy(item => item.HonoreeId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).First());

        var result = new Dictionary<int, DeactivationFacts>(honorees.Count);
        foreach (var honoree in honorees)
        {
            latestAudit.TryGetValue(honoree.Id, out var audit);
            latestQueue.TryGetValue(honoree.Id, out var queue);
            absorbed.TryGetValue(honoree.Id, out var merge);
            result[honoree.Id] = Describe(honoree, audit, queue, merge, survivors, grids, occupants);
        }

        return result;
    }

    private async Task<Occupancy> LoadOccupantsAsync(CancellationToken ct)
    {
        var rows = await db.Honorees.AsNoTracking()
            .Where(honoree => honoree.IsActive && honoree.DeletedDate == null)
            .Select(honoree => new Occupant(honoree.Id, honoree.FlagGridId, honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix, honoree.Nickname))
            .ToListAsync(ct);
        return new Occupancy(rows);
    }

    private static DeactivationFacts Describe(
        Honoree honoree,
        FlagGridRemovalAudit? audit,
        FlagRemovalQueueItem? queue,
        MergeHit? merge,
        IReadOnlyDictionary<int, Honoree> survivors,
        IReadOnlyDictionary<int, FlagGrid> grids,
        Occupancy occupants)
    {
        var events = new List<WhenReason>();
        if (audit is not null)
        {
            events.Add(new WhenReason(audit.OccurredUtc, audit.ActorEmail, Clean(audit.Reason), audit.FlagGridId, null));
        }

        if (queue is not null)
        {
            events.Add(new WhenReason(queue.CreatedUtc, queue.CreatedBy, QueueReason(queue), queue.FlagGridId, queue.FlagGridName));
        }

        string? mergeReason = null;
        if (merge is not null)
        {
            var survivor = survivors.TryGetValue(merge.SurvivorId, out var person)
                ? FlagRemovalQueue.DisplayName(person)
                : "another honoree";
            mergeReason = $"Merged into {survivor}.";
            events.Add(new WhenReason(merge.When, merge.Who, mergeReason, null, null));
        }

        if (honoree.DeletedDate is DateTime deleted)
        {
            events.Add(new WhenReason(deleted, honoree.DeletedBy, null, honoree.FlagGridId, null));
        }

        var withReason = events
            .Where(item => !string.IsNullOrWhiteSpace(item.Reason))
            .OrderByDescending(item => item.When)
            .FirstOrDefault();
        var chosen = withReason ?? events.OrderByDescending(item => item.When).FirstOrDefault();
        if (merge is not null && mergeReason is not null)
        {
            chosen = new WhenReason(merge.When, merge.Who, mergeReason, chosen?.FlagGridId, chosen?.FlagName);
        }

        int? flagId = honoree.FlagGridId;
        string? flagName = null;
        if (flagId is null)
        {
            var flagged = events
                .Where(item => item.FlagGridId is > 0 || !string.IsNullOrWhiteSpace(item.FlagName))
                .OrderByDescending(item => item.When)
                .FirstOrDefault();
            flagId = flagged?.FlagGridId;
            flagName = flagged?.FlagName;
        }

        if (flagId is int id && grids.TryGetValue(id, out var named))
        {
            flagName = named.FlagGridName;
        }

        string slotStatus;
        string? slotDetail;
        var canReactivate = merge is null;
        if (!canReactivate)
        {
            slotStatus = "merged";
            slotDetail = "Undo that merge from Merge history.";
        }
        else if (flagId is null && string.IsNullOrWhiteSpace(flagName))
        {
            slotStatus = "none";
            slotDetail = null;
        }
        else if (flagId is not int gridId || !grids.TryGetValue(gridId, out var grid) || grid.DeletedDate != null)
        {
            slotStatus = "missing";
            slotDetail = "This flag is no longer on the field.";
        }
        else if (OccupantOf(grid, occupants, honoree.Id) is { } occupant)
        {
            slotStatus = "taken";
            slotDetail = $"It is assigned to {occupant.Name}.";
        }
        else if (grid.Reserved)
        {
            slotStatus = "reserved";
            slotDetail = "This flag is reserved.";
        }
        else
        {
            slotStatus = "free";
            slotDetail = null;
        }

        return new DeactivationFacts(
            chosen?.When,
            DisplayPerson(chosen?.Who),
            string.IsNullOrWhiteSpace(chosen?.Reason) ? NotRecorded : chosen!.Reason!,
            flagId,
            string.IsNullOrWhiteSpace(flagName) ? null : flagName,
            slotStatus,
            slotDetail,
            canReactivate);
    }

    private static Occupant? OccupantOf(FlagGrid grid, Occupancy occupants, int exceptHonoreeId)
    {
        if (occupants.ByFlag.TryGetValue(grid.Id, out var seated) && seated.Id != exceptHonoreeId)
        {
            return seated;
        }

        if (grid.HonoreeId is int holder &&
            holder != exceptHonoreeId &&
            occupants.ById.TryGetValue(holder, out var pointed))
        {
            return pointed;
        }

        return null;
    }

    private static InactiveHonoreeDto ToDto(Honoree honoree, DeactivationFacts facts) =>
        new(
            honoree.Id,
            FlagRemovalQueue.DisplayName(honoree),
            string.IsNullOrWhiteSpace(honoree.Rank) ? null : honoree.Rank.Trim(),
            string.IsNullOrWhiteSpace(honoree.ServiceBranch?.ServiceBranchName) ? null : honoree.ServiceBranch!.ServiceBranchName.Trim(),
            facts.FlagGridId,
            facts.FlagName,
            facts.When,
            facts.By,
            facts.Reason,
            facts.SlotStatus,
            facts.SlotDetail,
            facts.CanReactivate);

    private static bool Matches(string query, InactiveHonoreeDto row)
    {
        if (query.Length == 0)
        {
            return true;
        }

        if (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (row.Branch?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return FlagPositionLabel.Matches(query, row.FormerFlagGridName);
    }

    private static string Blocked(string name, DeactivationFacts facts)
    {
        var flag = string.IsNullOrWhiteSpace(facts.FlagName) ? "The former flag" : $"Flag {facts.FlagName}";
        var why = facts.SlotStatus switch
        {
            "reserved" => $"{flag} is reserved. Choose an open flag.",
            "missing" => $"{flag} is no longer on the field. Choose an open flag.",
            "taken" => facts.SlotDetail is null
                ? $"{flag} is not open. Choose an open flag."
                : $"{flag} is not open. {facts.SlotDetail} Choose an open flag.",
            _ => "Choose an open flag."
        };
        return $"{name} was not restored. {why}";
    }

    private static string AuditReason(string flagName, string? formerName, bool sameSpot)
    {
        if (sameSpot || string.IsNullOrWhiteSpace(formerName) || string.Equals(formerName, flagName, StringComparison.OrdinalIgnoreCase))
        {
            return Clip($"Reactivated by an administrator onto {flagName}.", 1000);
        }

        return Clip($"Reactivated by an administrator onto {flagName}. Former flag {formerName} was not used.", 1000);
    }

    private static string? QueueReason(FlagRemovalQueueItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.SourceDetail))
        {
            return item.SourceDetail.Trim();
        }

        return item.Source switch
        {
            FlagRemovalQueue.Manual => "Removed from the flag grid.",
            FlagRemovalQueue.Move => "Moved to another flag.",
            FlagRemovalQueue.Merge => "Removed when records were merged.",
            _ => "Removed from the field."
        };
    }

    private static object Snapshot(Honoree honoree, FlagGrid? grid) => new
    {
        honoree = new
        {
            honoree.Id,
            honoree.IsActive,
            honoree.FlagGridId,
            honoree.DeletedDate,
            honoree.DeletedBy
        },
        flagGrid = grid is null ? null : new { grid.Id, grid.HonoreeId, grid.FlagGridName }
    };

    private static string DisplayPerson(string? value) =>
        string.IsNullOrWhiteSpace(value) ? NotRecorded : value.Trim();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeQuery(string? query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80];
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static List<int> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsSlotConflict(DbUpdateException ex)
    {
        var text = ex.InnerException?.Message ?? ex.Message;
        return text.Contains("UX_Honorees_OneActiveFlagGrid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("UNIQUE constraint failed: Honorees.FlagGridId", StringComparison.OrdinalIgnoreCase);
    }

    private static InactiveHonoreeReactivateItemResultDto Result(
        int honoreeId,
        string name,
        string outcome,
        string code,
        string message,
        int? flagGridId = null,
        string? flagGridName = null,
        bool reprintQueued = false,
        bool removalCanceled = false,
        string? pdfWarning = null) =>
        new(honoreeId, name, outcome, code, message, flagGridId, flagGridName, reprintQueued, removalCanceled, pdfWarning);

    private sealed record DeactivationFacts(
        DateTime? When,
        string By,
        string Reason,
        int? FlagGridId,
        string? FlagName,
        string SlotStatus,
        string? SlotDetail,
        bool CanReactivate);

    private sealed record WhenReason(DateTime When, string? Who, string? Reason, int? FlagGridId, string? FlagName);

    private sealed record MergeRow(int SurvivorId, string AbsorbedHonoreeIds, DateTime MergedUtc, string MergedBy);

    private sealed record MergeHit(int SurvivorId, DateTime When, string Who);

    private sealed record Occupant(int Id, int? FlagGridId, string FirstName, string? MiddleName, string LastName, string? Suffix, string? Nickname)
    {
        public string Name => FlagRemovalQueue.DisplayName(new Honoree
        {
            FirstName = FirstName,
            MiddleName = MiddleName,
            LastName = LastName,
            Suffix = Suffix,
            Nickname = Nickname
        });
    }

    private sealed class Occupancy(IReadOnlyList<Occupant> rows)
    {
        public Dictionary<int, Occupant> ById { get; } = rows.ToDictionary(row => row.Id);

        public Dictionary<int, Occupant> ByFlag { get; } = rows
            .Where(row => row.FlagGridId is > 0)
            .GroupBy(row => row.FlagGridId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.LastName).ThenBy(row => row.FirstName).First());
    }
}
