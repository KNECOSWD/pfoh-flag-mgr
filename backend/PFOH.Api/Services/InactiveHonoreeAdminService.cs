using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

/// <summary>
/// Lists inactive honorees and sends them to the approval queue with no flag.
/// A flag is chosen later, through the existing approval and assignment flow.
/// The public reactivation-link flow is not used here. No email is sent.
/// </summary>
public sealed class InactiveHonoreeAdminService(
    PfohDbContext db,
    InactiveRestoreGate restoreGate)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 50;
    public const int MaxBatch = 50;
    public const string NotRecorded = "Not recorded";
    public const string ApprovalQueueNote = "Reactivated to approval queue";

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

    public async Task<InactiveHonoreeReactivateResultDto> ReactivateAsync(
        IReadOnlyList<InactiveHonoreeReactivateItem> items,
        AdminActor actor,
        CancellationToken ct)
    {
        var results = new List<InactiveHonoreeReactivateItemResultDto>(items.Count);
        foreach (var item in items)
        {
            results.Add(await ReactivateOneAsync(item.HonoreeId, actor, ct));
        }

        return new InactiveHonoreeReactivateResultDto(results);
    }

    private async Task<InactiveHonoreeReactivateItemResultDto> ReactivateOneAsync(
        int honoreeId,
        AdminActor actor,
        CancellationToken ct)
    {
        if (restoreGate.BeforeRestore is not null)
        {
            await restoreGate.BeforeRestore(ct);
        }

        if (!db.Database.IsRelational())
        {
            return await QueueCoreAsync(honoreeId, actor, ct);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var result = await QueueCoreAsync(honoreeId, actor, ct);
            if (result.Outcome == "reactivated")
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<InactiveHonoreeReactivateItemResultDto> QueueCoreAsync(
        int honoreeId,
        AdminActor actor,
        CancellationToken ct)
    {
        await FlagGridRemovalAuditSchema.EnsureAsync(db, ct);
        var honoree = await db.Honorees
            .Include(row => row.ServiceBranch)
            .FirstOrDefaultAsync(row => row.Id == honoreeId, ct);
        if (honoree is null)
        {
            return Result(honoreeId, $"Honoree {honoreeId}", "not-found", "not-found", "That honoree was not found.");
        }

        var name = FlagRemovalQueue.DisplayName(honoree);
        if (honoree.IsActive)
        {
            return Result(honoree.Id, name, "blocked", "already-active", $"{name} was not sent. This honoree is already active.");
        }

        var facts = (await LoadFactsAsync([honoree], ct))[honoree.Id];
        if (!facts.CanReactivate)
        {
            var code = facts.InApprovalQueue ? "already-pending" : "merged";
            var message = facts.InApprovalQueue
                ? $"{name} was not sent again. This honoree is already in the approval queue."
                : $"{name} was not sent. {facts.Reason} Undo that merge from Merge history.";
            return Result(honoree.Id, name, "blocked", code, message);
        }

        var alreadyPending = await db.HonoreeChangeRequests.AnyAsync(request =>
            request.HonoreeId == honoree.Id && request.RequestStatus == "Submitted", ct);
        if (alreadyPending)
        {
            return Result(
                honoree.Id,
                name,
                "blocked",
                "already-pending",
                $"{name} was not sent again. This honoree is already in the approval queue.");
        }

        var now = DateTime.UtcNow;
        var before = Snapshot(honoree);
        if (honoree.FlagGridId is int linked)
        {
            var linkedGrid = await db.FlagGrids.FirstOrDefaultAsync(grid => grid.Id == linked, ct);
            if (linkedGrid is not null && linkedGrid.HonoreeId == honoree.Id)
            {
                linkedGrid.HonoreeId = null;
                linkedGrid.ModifiedBy = actor.Email;
                linkedGrid.ModifiedDate = now;
            }
        }

        honoree.FlagGridId = null;
        honoree.DeletedDate = null;
        honoree.DeletedBy = null;
        honoree.IsActive = false;
        honoree.ModifiedBy = actor.Email;
        honoree.ModifiedDate = now;

        var claim = new FlagClaim
        {
            FlagGridId = null,
            HonoreeId = honoree.Id,
            ExternalUserObjectId = string.IsNullOrWhiteSpace(actor.ObjectId)
                ? $"admin-reactivate:{Guid.NewGuid():N}"
                : actor.ObjectId,
            ExternalUserEmail = actor.Email,
            ExternalUserName = "PFOH Admin",
            ClaimStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        };
        var change = new HonoreeChangeRequest
        {
            FlagClaim = claim,
            FlagGridId = null,
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
            SubmitterEmailAddress = string.IsNullOrWhiteSpace(honoree.EmailAddress) ? actor.Email : honoree.EmailAddress,
            RequestStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now,
            ReviewNotes = ApprovalQueueNote
        };
        db.FlagClaims.Add(claim);
        db.HonoreeChangeRequests.Add(change);
        db.FlagGridRemovalAudits.Add(new FlagGridRemovalAudit
        {
            HonoreeId = honoree.Id,
            FlagGridId = null,
            Action = RestoreAction,
            ActorEmail = actor.Email,
            ActorObjectId = actor.ObjectId,
            OccurredUtc = now,
            Reason = ApprovalQueueNote,
            BeforeState = JsonSerializer.Serialize(before, Json),
            AfterState = JsonSerializer.Serialize(Snapshot(honoree), Json)
        });
        await db.SaveChangesAsync(ct);
        return Result(
            honoree.Id,
            name,
            "reactivated",
            "ok",
            $"{name} will go to the approval queue with no flag assigned.",
            change.Id);
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
        var pending = ids.Count == 0
            ? new HashSet<int>()
            : (await db.HonoreeChangeRequests.AsNoTracking()
                .Where(request => request.RequestStatus == "Submitted" && request.HonoreeId != null && ids.Contains(request.HonoreeId.Value))
                .Select(request => request.HonoreeId!.Value)
                .Distinct()
                .ToListAsync(ct)).ToHashSet();
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
            result[honoree.Id] = Describe(honoree, audit, queue, merge, survivors, grids, pending.Contains(honoree.Id));
        }

        return result;
    }

    private static DeactivationFacts Describe(
        Honoree honoree,
        FlagGridRemovalAudit? audit,
        FlagRemovalQueueItem? queue,
        MergeHit? merge,
        IReadOnlyDictionary<int, Honoree> survivors,
        IReadOnlyDictionary<int, FlagGrid> grids,
        bool inApprovalQueue)
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

        string? blockReason = null;
        var canReactivate = merge is null && !inApprovalQueue;
        if (merge is not null)
        {
            blockReason = "Undo that merge from Merge history.";
        }
        else if (inApprovalQueue)
        {
            blockReason = "Already in the approval queue.";
        }

        return new DeactivationFacts(
            chosen?.When,
            DisplayPerson(chosen?.Who),
            string.IsNullOrWhiteSpace(chosen?.Reason) ? NotRecorded : chosen!.Reason!,
            flagId,
            string.IsNullOrWhiteSpace(flagName) ? null : flagName,
            canReactivate,
            inApprovalQueue,
            blockReason);
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
            facts.CanReactivate,
            facts.BlockReason);

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

    private static object Snapshot(Honoree honoree) => new
    {
        honoree = new
        {
            honoree.Id,
            honoree.IsActive,
            honoree.FlagGridId,
            honoree.DeletedDate,
            honoree.DeletedBy
        },
        approvalQueue = honoree.IsActive ? null : ApprovalQueueNote
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

    private static InactiveHonoreeReactivateItemResultDto Result(
        int honoreeId,
        string name,
        string outcome,
        string code,
        string message,
        int? changeRequestId = null) =>
        new(honoreeId, name, outcome, code, message, changeRequestId);

    private sealed record DeactivationFacts(
        DateTime? When,
        string By,
        string Reason,
        int? FlagGridId,
        string? FlagName,
        bool CanReactivate,
        bool InApprovalQueue,
        string? BlockReason);

    private sealed record WhenReason(DateTime When, string? Who, string? Reason, int? FlagGridId, string? FlagName);

    private sealed record MergeRow(int SurvivorId, string AbsorbedHonoreeIds, DateTime MergedUtc, string MergedBy);

    private sealed record MergeHit(int SurvivorId, DateTime When, string Who);
}
