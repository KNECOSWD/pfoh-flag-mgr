using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed record MergeHistoryRecord(
    int Id,
    int SurvivorId,
    string SurvivorName,
    IReadOnlyList<MergeHistoryAbsorbed> Absorbed,
    string MergedBy,
    DateTime MergedUtc,
    string? UndoneBy,
    DateTime? UndoneUtc);

public sealed record MergeHistoryAbsorbed(int Id, string Name);

/// <summary>
/// Writes one audit row in the merge transaction and puts that merge back.
/// The administrator name comes from the caller, which reads the token.
/// </summary>
public static class HonoreeMergeAuditLog
{
    public const string AlreadyUndone = "This merge was already undone.";
    public const string SurvivorEdited = "This merge cannot be undone because the surviving record was edited after the merge.";
    public const string PostMergeMissing = "This merge cannot be undone because the post-merge state was not recorded.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<HonoreeMergeAudit> CaptureAsync(
        PfohDbContext db,
        Honoree survivor,
        IReadOnlyList<Honoree> confirmed,
        IReadOnlyDictionary<string, int> fields,
        MergeCustom? custom,
        string adminName,
        DateTime now,
        Guid clientRequestId,
        CancellationToken ct)
    {
        custom ??= new MergeCustom();
        var honoreeIds = confirmed.Select(honoree => honoree.Id).Distinct().ToList();
        var gridIds = confirmed
            .Select(honoree => honoree.FlagGridId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        var byId = confirmed.ToDictionary(honoree => honoree.Id);
        var kept = HonoreeMerge.KeptGridId(byId, fields, custom);
        if (kept is int keptId && !gridIds.Contains(keptId))
        {
            gridIds.Add(keptId);
        }

        var grids = gridIds.Count == 0
            ? new List<FlagGrid>()
            : await db.FlagGrids.Where(grid => gridIds.Contains(grid.Id)).ToListAsync(ct);
        var flagClaims = await db.FlagClaims
            .Where(claim =>
                (claim.HonoreeId.HasValue && honoreeIds.Contains(claim.HonoreeId.Value)) ||
                (claim.FlagGridId.HasValue && gridIds.Contains(claim.FlagGridId.Value)))
            .ToListAsync(ct);
        var claimIds = flagClaims.Select(claim => claim.Id).ToList();
        var honoreeClaims = await db.HonoreeClaims
            .Where(claim => honoreeIds.Contains(claim.HonoreeId))
            .ToListAsync(ct);
        var changes = await db.HonoreeChangeRequests
            .Where(change =>
                (change.HonoreeId.HasValue && honoreeIds.Contains(change.HonoreeId.Value)) ||
                (change.FlagGridId.HasValue && gridIds.Contains(change.FlagGridId.Value)) ||
                claimIds.Contains(change.FlagClaimId))
            .ToListAsync(ct);

        var before = new MergeBeforeState(
            confirmed.Select(CopyHonoree).ToList(),
            grids.Select(CopyGrid).ToList(),
            flagClaims.Select(CopyFlagClaim).ToList(),
            honoreeClaims.Select(CopyClaim).ToList(),
            changes.Select(change => new ChangeSnap(change.Id, change.HonoreeId, change.FlagGridId)).ToList());

        var absorbed = confirmed.Where(honoree => honoree.Id != survivor.Id).Select(honoree => honoree.Id).ToList();
        var audit = new HonoreeMergeAudit
        {
            ClientRequestId = clientRequestId,
            SurvivorId = survivor.Id,
            AbsorbedHonoreeIds = JsonSerializer.Serialize(absorbed, Json),
            BeforeState = JsonSerializer.Serialize(before, Json),
            AfterSurvivorState = "{}",
            AfterState = "{}",
            CreatedHonoreeClaimIds = "[]",
            MergedBy = TrimAdmin(adminName),
            MergedUtc = now,
            SurvivorModifiedUtc = now
        };
        db.HonoreeMergeAudits.Add(audit);
        return audit;
    }

    public static void NoteSurvivorAfter(HonoreeMergeAudit audit, Honoree survivor)
    {
        audit.AfterSurvivorState = JsonSerializer.Serialize(CopyHonoree(survivor), Json);
        audit.SurvivorModifiedUtc = survivor.ModifiedDate;
    }

    public static async Task NoteCreatedClaimsAsync(
        PfohDbContext db,
        HonoreeMergeAudit audit,
        IReadOnlyList<int> honoreeIds,
        CancellationToken ct)
    {
        var before = JsonSerializer.Deserialize<MergeBeforeState>(audit.BeforeState, Json);
        var known = before?.HonoreeClaims.Select(claim => claim.Id).ToHashSet() ?? new HashSet<int>();
        var current = await db.HonoreeClaims
            .Where(claim => honoreeIds.Contains(claim.HonoreeId))
            .Select(claim => claim.Id)
            .ToListAsync(ct);
        var created = current.Where(id => !known.Contains(id)).ToList();
        audit.CreatedHonoreeClaimIds = JsonSerializer.Serialize(created, Json);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads the rows the merge touched back from the database and stores that
    /// post-merge state. The read happens after SaveChanges, so a SQL datetime
    /// column's rounded value is what undo later compares.
    /// </summary>
    public static async Task NoteAfterStateAsync(PfohDbContext db, HonoreeMergeAudit audit, CancellationToken ct)
    {
        var before = JsonSerializer.Deserialize<MergeBeforeState>(audit.BeforeState, Json);
        if (before is null)
        {
            return;
        }

        var honoreeIds = before.Honorees.Select(honoree => honoree.Id).ToList();
        var gridIds = before.Grids.Select(grid => grid.Id).ToList();
        var flagClaimIds = before.FlagClaims.Select(claim => claim.Id).ToList();
        var honoreeClaimIds = before.HonoreeClaims.Select(claim => claim.Id).ToList();
        var created = JsonSerializer.Deserialize<List<int>>(audit.CreatedHonoreeClaimIds, Json) ?? new List<int>();
        honoreeClaimIds.AddRange(created.Where(id => !honoreeClaimIds.Contains(id)));
        var changeIds = before.ChangeRequests.Select(change => change.Id).ToList();

        var honorees = await db.Honorees.AsNoTracking().Where(honoree => honoreeIds.Contains(honoree.Id)).ToListAsync(ct);
        var grids = gridIds.Count == 0
            ? new List<FlagGrid>()
            : await db.FlagGrids.AsNoTracking().Where(grid => gridIds.Contains(grid.Id)).ToListAsync(ct);
        var flagClaims = flagClaimIds.Count == 0
            ? new List<FlagClaim>()
            : await db.FlagClaims.AsNoTracking().Where(claim => flagClaimIds.Contains(claim.Id)).ToListAsync(ct);
        var honoreeClaims = honoreeClaimIds.Count == 0
            ? new List<HonoreeClaim>()
            : await db.HonoreeClaims.AsNoTracking().Where(claim => honoreeClaimIds.Contains(claim.Id)).ToListAsync(ct);
        var changes = changeIds.Count == 0
            ? new List<HonoreeChangeRequest>()
            : await db.HonoreeChangeRequests.AsNoTracking().Where(change => changeIds.Contains(change.Id)).ToListAsync(ct);

        var after = new MergeBeforeState(
            honorees.Select(CopyHonoree).ToList(),
            grids.Select(CopyGrid).ToList(),
            flagClaims.Select(CopyFlagClaim).ToList(),
            honoreeClaims.Select(CopyClaim).ToList(),
            changes.Select(change => new ChangeSnap(change.Id, change.HonoreeId, change.FlagGridId)).ToList());
        audit.AfterState = JsonSerializer.Serialize(after, Json);
        var survivor = honorees.FirstOrDefault(honoree => honoree.Id == audit.SurvivorId);
        if (survivor is not null)
        {
            audit.AfterSurvivorState = JsonSerializer.Serialize(CopyHonoree(survivor), Json);
            audit.SurvivorModifiedUtc = survivor.ModifiedDate;
        }

        await db.SaveChangesAsync(ct);
    }

    public static async Task<IReadOnlyList<MergeHistoryRecord>> ListAsync(PfohDbContext db, CancellationToken ct)
    {
        var rows = await db.HonoreeMergeAudits
            .AsNoTracking()
            .OrderByDescending(row => row.MergedUtc)
            .ThenByDescending(row => row.Id)
            .ToListAsync(ct);
        return rows.Select(ToHistory).ToList();
    }

    /// <summary>
    /// Returns null when the merge was undone. Otherwise a 409 message, or "missing".
    /// </summary>
    public static async Task<string?> UndoAsync(PfohDbContext db, int id, string adminName, CancellationToken ct)
    {
        var audit = await db.HonoreeMergeAudits.FirstOrDefaultAsync(row => row.Id == id, ct);
        if (audit is null)
        {
            return "missing";
        }

        if (audit.UndoneUtc != null)
        {
            return AlreadyUndone;
        }

        var before = JsonSerializer.Deserialize<MergeBeforeState>(audit.BeforeState, Json);
        var after = JsonSerializer.Deserialize<MergeBeforeState>(audit.AfterState, Json);
        if (before is null || after?.Honorees is null || after.Honorees.Count == 0)
        {
            return PostMergeMissing;
        }

        var honoreeIds = before.Honorees.Select(honoree => honoree.Id).ToList();
        var honorees = await db.Honorees.Where(honoree => honoreeIds.Contains(honoree.Id)).ToListAsync(ct);
        var gridIds = (after.Grids ?? new List<GridSnap>()).Select(grid => grid.Id).ToList();
        var grids = gridIds.Count == 0
            ? new List<FlagGrid>()
            : await db.FlagGrids.Where(grid => gridIds.Contains(grid.Id)).ToListAsync(ct);
        var claimIds = (after.FlagClaims ?? new List<FlagClaimSnap>()).Select(claim => claim.Id).ToList();
        var flagClaims = claimIds.Count == 0
            ? new List<FlagClaim>()
            : await db.FlagClaims.Where(claim => claimIds.Contains(claim.Id)).ToListAsync(ct);
        var honoreeClaimIds = (after.HonoreeClaims ?? new List<HonoreeClaimSnap>()).Select(claim => claim.Id).ToList();
        var honoreeClaims = honoreeClaimIds.Count == 0
            ? new List<HonoreeClaim>()
            : await db.HonoreeClaims.Where(claim => honoreeClaimIds.Contains(claim.Id)).ToListAsync(ct);
        var changeIds = (after.ChangeRequests ?? new List<ChangeSnap>()).Select(change => change.Id).ToList();
        var changes = changeIds.Count == 0
            ? new List<HonoreeChangeRequest>()
            : await db.HonoreeChangeRequests.Where(change => changeIds.Contains(change.Id)).ToListAsync(ct);

        var blocked = await PostMergeDriftAsync(db, audit.SurvivorId, after, honorees, grids, flagClaims, honoreeClaims, changes, ct);
        if (blocked is not null)
        {
            return blocked;
        }

        var added = await AddedAfterMergeAsync(db, before, after, ct);
        if (added is not null)
        {
            return added;
        }

        var createdIds = JsonSerializer.Deserialize<List<int>>(audit.CreatedHonoreeClaimIds, Json) ?? new List<int>();
        if (createdIds.Count > 0)
        {
            var created = await db.HonoreeClaims.Where(claim => createdIds.Contains(claim.Id)).ToListAsync(ct);
            db.HonoreeClaims.RemoveRange(created);
        }

        foreach (var snap in before.Honorees)
        {
            ApplyHonoree(honorees.Single(honoree => honoree.Id == snap.Id), snap);
        }

        foreach (var snap in before.Grids)
        {
            var grid = grids.SingleOrDefault(item => item.Id == snap.Id);
            if (grid is null)
            {
                continue;
            }

            grid.HonoreeId = snap.HonoreeId;
            grid.ModifiedBy = snap.ModifiedBy;
            grid.ModifiedDate = snap.ModifiedDate;
        }

        foreach (var snap in before.FlagClaims)
        {
            var claim = flagClaims.SingleOrDefault(item => item.Id == snap.Id);
            if (claim is null)
            {
                continue;
            }

            claim.HonoreeId = snap.HonoreeId;
            claim.FlagGridId = snap.FlagGridId;
        }

        foreach (var snap in before.HonoreeClaims)
        {
            var claim = honoreeClaims.SingleOrDefault(item => item.Id == snap.Id);
            if (claim is null)
            {
                continue;
            }

            claim.HonoreeId = snap.HonoreeId;
            claim.UserId = snap.UserId;
            claim.ClaimedUtc = snap.ClaimedUtc;
            claim.Email = snap.Email;
            claim.DisplayName = snap.DisplayName;
            claim.ClaimStatus = snap.ClaimStatus;
            claim.SourceFlagClaimId = snap.SourceFlagClaimId;
        }

        foreach (var snap in before.ChangeRequests)
        {
            var change = changes.SingleOrDefault(item => item.Id == snap.Id);
            if (change is null)
            {
                continue;
            }

            change.HonoreeId = snap.HonoreeId;
            change.FlagGridId = snap.FlagGridId;
        }

        var undoneUtc = DateTime.UtcNow;
        audit.UndoneBy = TrimAdmin(adminName);
        audit.UndoneUtc = undoneUtc;
        await FlagRemovalQueue.OnMergeUndoneAsync(db, audit, adminName, undoneUtc, ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static MergeHistoryRecord ToHistory(HonoreeMergeAudit row)
    {
        var before = JsonSerializer.Deserialize<MergeBeforeState>(row.BeforeState, Json);
        var names = before?.Honorees.ToDictionary(honoree => honoree.Id, Display) ?? new Dictionary<int, string>();
        var absorbedIds = JsonSerializer.Deserialize<List<int>>(row.AbsorbedHonoreeIds, Json) ?? new List<int>();
        return new MergeHistoryRecord(
            row.Id,
            row.SurvivorId,
            names.TryGetValue(row.SurvivorId, out var survivor) ? survivor : $"Record {row.SurvivorId}",
            absorbedIds.Select(id => new MergeHistoryAbsorbed(id, names.TryGetValue(id, out var name) ? name : $"Record {id}")).ToList(),
            row.MergedBy,
            row.MergedUtc,
            row.UndoneBy,
            row.UndoneUtc);
    }

    /// <summary>
    /// Returns a 409 message when a row the merge left behind no longer matches
    /// the post-merge snapshot. Nothing has been written when this returns.
    /// </summary>
    private static async Task<string?> PostMergeDriftAsync(
        PfohDbContext db,
        int survivorId,
        MergeBeforeState after,
        List<Honoree> honorees,
        List<FlagGrid> grids,
        List<FlagClaim> flagClaims,
        List<HonoreeClaim> honoreeClaims,
        List<HonoreeChangeRequest> changes,
        CancellationToken ct)
    {
        foreach (var snap in after.Honorees)
        {
            var honoree = honorees.SingleOrDefault(item => item.Id == snap.Id);
            var survivor = snap.Id == survivorId;
            if (honoree is null)
            {
                return survivor
                    ? SurvivorEdited
                    : "This merge cannot be undone because an absorbed record was removed after the merge.";
            }

            if (!SameSurvivor(honoree, snap))
            {
                return survivor
                    ? SurvivorEdited
                    : "This merge cannot be undone because an absorbed record was edited after the merge.";
            }
        }

        foreach (var snap in after.Grids ?? new List<GridSnap>())
        {
            var grid = grids.SingleOrDefault(item => item.Id == snap.Id);
            var name = string.IsNullOrWhiteSpace(snap.Name) ? $"grid {snap.Id}" : snap.Name;
            if (grid is null || grid.DeletedDate != snap.DeletedDate)
            {
                return $"This merge cannot be undone because flag grid {name} was removed after the merge.";
            }

            if (grid.HonoreeId != snap.HonoreeId)
            {
                return snap.HonoreeId is null
                    ? $"This merge cannot be undone because flag grid {name} was assigned after the merge."
                    : $"This merge cannot be undone because flag grid {name} was reassigned after the merge.";
            }

            var holderId = snap.HonoreeId;
            var occupied = holderId is int holder
                ? await db.Honorees.AsNoTracking().AnyAsync(
                    honoree => honoree.FlagGridId == snap.Id && honoree.DeletedDate == null && honoree.Id != holder,
                    ct)
                : await db.Honorees.AsNoTracking().AnyAsync(
                    honoree => honoree.FlagGridId == snap.Id && honoree.DeletedDate == null,
                    ct);
            if (occupied)
            {
                return $"This merge cannot be undone because flag grid {name} was assigned after the merge.";
            }

            if (grid.ModifiedBy != snap.ModifiedBy || grid.ModifiedDate != snap.ModifiedDate || grid.FlagGridName != snap.Name)
            {
                return $"This merge cannot be undone because flag grid {name} was edited after the merge.";
            }
        }

        foreach (var snap in after.FlagClaims ?? new List<FlagClaimSnap>())
        {
            var claim = flagClaims.SingleOrDefault(item => item.Id == snap.Id);
            if (claim is null)
            {
                return $"This merge cannot be undone because flag claim {snap.Id} was removed after the merge.";
            }

            if (claim.HonoreeId != snap.HonoreeId ||
                claim.FlagGridId != snap.FlagGridId ||
                claim.ExternalUserObjectId != snap.ExternalUserObjectId ||
                claim.ExternalUserEmail != snap.ExternalUserEmail ||
                claim.ExternalUserName != snap.ExternalUserName ||
                claim.ClaimStatus != snap.ClaimStatus)
            {
                return $"This merge cannot be undone because flag claim {snap.Id} was edited after the merge.";
            }
        }

        foreach (var snap in after.HonoreeClaims ?? new List<HonoreeClaimSnap>())
        {
            var claim = honoreeClaims.SingleOrDefault(item => item.Id == snap.Id);
            if (claim is null)
            {
                return $"This merge cannot be undone because honoree claim {snap.Id} was removed after the merge.";
            }

            if (claim.HonoreeId != snap.HonoreeId ||
                claim.UserId != snap.UserId ||
                claim.ClaimedUtc != snap.ClaimedUtc ||
                claim.Email != snap.Email ||
                claim.DisplayName != snap.DisplayName ||
                claim.ClaimStatus != snap.ClaimStatus ||
                claim.SourceFlagClaimId != snap.SourceFlagClaimId)
            {
                return $"This merge cannot be undone because honoree claim {snap.Id} was edited after the merge.";
            }
        }

        foreach (var snap in after.ChangeRequests ?? new List<ChangeSnap>())
        {
            var change = changes.SingleOrDefault(item => item.Id == snap.Id);
            if (change is null)
            {
                return $"This merge cannot be undone because change request {snap.Id} was removed after the merge.";
            }

            if (change.HonoreeId != snap.HonoreeId || change.FlagGridId != snap.FlagGridId)
            {
                return $"This merge cannot be undone because change request {snap.Id} was edited after the merge.";
            }
        }

        return null;
    }

    /// <summary>
    /// A row created after the merge is absent from the post-merge snapshot.
    /// Restoring the old grid or claim on top of it would put two rows on one spot.
    /// </summary>
    private static async Task<string?> AddedAfterMergeAsync(
        PfohDbContext db,
        MergeBeforeState before,
        MergeBeforeState after,
        CancellationToken ct)
    {
        var mergedIds = before.Honorees.Select(honoree => honoree.Id)
            .Concat(after.Honorees.Select(honoree => honoree.Id))
            .Distinct()
            .ToList();
        var gridNames = new Dictionary<int, string>();
        foreach (var grid in (before.Grids ?? new List<GridSnap>()).Concat(after.Grids ?? new List<GridSnap>()))
        {
            gridNames[grid.Id] = string.IsNullOrWhiteSpace(grid.Name) ? $"grid {grid.Id}" : grid.Name;
        }

        var gridIds = gridNames.Keys.ToList();
        var knownFlagClaims = (after.FlagClaims ?? new List<FlagClaimSnap>()).Select(claim => claim.Id).ToList();
        var knownHonoreeClaims = (after.HonoreeClaims ?? new List<HonoreeClaimSnap>()).Select(claim => claim.Id).ToList();
        var knownChanges = (after.ChangeRequests ?? new List<ChangeSnap>()).Select(change => change.Id).ToList();

        var flagClaim = await db.FlagClaims.AsNoTracking()
            .Where(claim =>
                !knownFlagClaims.Contains(claim.Id) &&
                ((claim.FlagGridId != null && gridIds.Contains(claim.FlagGridId.Value)) ||
                 (claim.HonoreeId != null && mergedIds.Contains(claim.HonoreeId.Value))))
            .Select(claim => new { claim.Id, claim.FlagGridId })
            .FirstOrDefaultAsync(ct);
        if (flagClaim is not null)
        {
            var spot = flagClaim.FlagGridId is int gridId && gridNames.TryGetValue(gridId, out var name)
                ? $" on flag grid {name}"
                : "";
            return $"This merge cannot be undone because flag claim {flagClaim.Id} was added{spot} after the merge.";
        }

        var honoreeClaimId = await db.HonoreeClaims.AsNoTracking()
            .Where(claim => mergedIds.Contains(claim.HonoreeId) && !knownHonoreeClaims.Contains(claim.Id))
            .Select(claim => claim.Id)
            .FirstOrDefaultAsync(ct);
        if (honoreeClaimId != 0)
        {
            return $"This merge cannot be undone because honoree claim {honoreeClaimId} was added after the merge.";
        }

        var changeRequestId = await db.HonoreeChangeRequests.AsNoTracking()
            .Where(change =>
                !knownChanges.Contains(change.Id) &&
                ((change.HonoreeId != null && mergedIds.Contains(change.HonoreeId.Value)) ||
                 (change.FlagGridId != null && gridIds.Contains(change.FlagGridId.Value))))
            .Select(change => change.Id)
            .FirstOrDefaultAsync(ct);
        if (changeRequestId != 0)
        {
            return $"This merge cannot be undone because change request {changeRequestId} was added after the merge.";
        }

        return null;
    }

    private static bool SameSurvivor(Honoree current, HonoreeSnap after) =>
        current.SponsorId == after.SponsorId &&
        current.FlagGridId == after.FlagGridId &&
        current.KIA == after.Kia &&
        current.Description == after.Description &&
        current.FirstName == after.FirstName &&
        current.MiddleName == after.MiddleName &&
        current.LastName == after.LastName &&
        current.Suffix == after.Suffix &&
        current.Nickname == after.Nickname &&
        current.Salutation == after.Salutation &&
        current.Rank == after.Rank &&
        current.StartYear == after.StartYear &&
        current.EndYear == after.EndYear &&
        current.DatesUserEntry == after.DatesUserEntry &&
        current.NameUserEntry == after.NameUserEntry &&
        current.ConflictsServed == after.ConflictsServed &&
        current.Awards == after.Awards &&
        current.PhoneNumber == after.PhoneNumber &&
        current.EmailAddress == after.EmailAddress &&
        current.ServiceBranchCategoryId == after.ServiceBranchCategoryId &&
        current.ServiceBranchId == after.ServiceBranchId &&
        current.FlagLocation == after.FlagLocation &&
        current.IsActive == after.IsActive &&
        current.PhotoFileName == after.PhotoFileName &&
        current.LegacyKey == after.LegacyKey &&
        current.CreatedBy == after.CreatedBy &&
        current.CreatedDate == after.CreatedDate &&
        current.ModifiedBy == after.ModifiedBy &&
        current.ModifiedDate == after.ModifiedDate &&
        current.DeletedBy == after.DeletedBy &&
        current.DeletedDate == after.DeletedDate;

    private static HonoreeSnap CopyHonoree(Honoree honoree) => new(
        honoree.Id,
        honoree.SponsorId,
        honoree.FlagGridId,
        honoree.KIA,
        honoree.Description,
        honoree.FirstName,
        honoree.MiddleName,
        honoree.LastName,
        honoree.Suffix,
        honoree.Nickname,
        honoree.Salutation,
        honoree.Rank,
        honoree.StartYear,
        honoree.EndYear,
        honoree.DatesUserEntry,
        honoree.NameUserEntry,
        honoree.ConflictsServed,
        honoree.Awards,
        honoree.PhoneNumber,
        honoree.EmailAddress,
        honoree.ServiceBranchCategoryId,
        honoree.ServiceBranchId,
        honoree.FlagLocation,
        honoree.IsActive,
        honoree.PhotoFileName,
        honoree.LegacyKey,
        honoree.CreatedBy,
        honoree.CreatedDate,
        honoree.ModifiedBy,
        honoree.ModifiedDate,
        honoree.DeletedBy,
        honoree.DeletedDate);

    private static void ApplyHonoree(Honoree honoree, HonoreeSnap snap)
    {
        honoree.SponsorId = snap.SponsorId;
        honoree.FlagGridId = snap.FlagGridId;
        honoree.KIA = snap.Kia;
        honoree.Description = snap.Description;
        honoree.FirstName = snap.FirstName;
        honoree.MiddleName = snap.MiddleName;
        honoree.LastName = snap.LastName;
        honoree.Suffix = snap.Suffix;
        honoree.Nickname = snap.Nickname;
        honoree.Salutation = snap.Salutation;
        honoree.Rank = snap.Rank;
        honoree.StartYear = snap.StartYear;
        honoree.EndYear = snap.EndYear;
        honoree.DatesUserEntry = snap.DatesUserEntry;
        honoree.NameUserEntry = snap.NameUserEntry;
        honoree.ConflictsServed = snap.ConflictsServed;
        honoree.Awards = snap.Awards;
        honoree.PhoneNumber = snap.PhoneNumber;
        honoree.EmailAddress = snap.EmailAddress;
        honoree.ServiceBranchCategoryId = snap.ServiceBranchCategoryId;
        honoree.ServiceBranchId = snap.ServiceBranchId;
        honoree.FlagLocation = snap.FlagLocation;
        honoree.IsActive = snap.IsActive;
        honoree.PhotoFileName = snap.PhotoFileName;
        honoree.LegacyKey = snap.LegacyKey;
        honoree.CreatedBy = snap.CreatedBy;
        honoree.CreatedDate = snap.CreatedDate;
        honoree.ModifiedBy = snap.ModifiedBy;
        honoree.ModifiedDate = snap.ModifiedDate;
        honoree.DeletedBy = snap.DeletedBy;
        honoree.DeletedDate = snap.DeletedDate;
    }

    private static GridSnap CopyGrid(FlagGrid grid) =>
        new(grid.Id, grid.HonoreeId, grid.ModifiedBy, grid.ModifiedDate, grid.FlagGridName, grid.DeletedDate);

    private static FlagClaimSnap CopyFlagClaim(FlagClaim claim) => new(
        claim.Id,
        claim.HonoreeId,
        claim.FlagGridId,
        claim.ExternalUserObjectId,
        claim.ExternalUserEmail,
        claim.ExternalUserName,
        claim.ClaimStatus);

    private static HonoreeClaimSnap CopyClaim(HonoreeClaim claim) => new(
        claim.Id,
        claim.HonoreeId,
        claim.UserId,
        claim.ClaimedUtc,
        claim.Email,
        claim.DisplayName,
        claim.ClaimStatus,
        claim.SourceFlagClaimId);

    private static string Display(HonoreeSnap honoree) =>
        string.Join(" ", new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string TrimAdmin(string adminName)
    {
        var text = (adminName ?? string.Empty).Trim();
        return text.Length <= 255 ? text : text[..255];
    }

    private sealed record MergeBeforeState(
        List<HonoreeSnap> Honorees,
        List<GridSnap> Grids,
        List<FlagClaimSnap> FlagClaims,
        List<HonoreeClaimSnap> HonoreeClaims,
        List<ChangeSnap> ChangeRequests);

    private sealed record HonoreeSnap(
        int Id,
        int? SponsorId,
        int? FlagGridId,
        bool Kia,
        string Description,
        string FirstName,
        string? MiddleName,
        string LastName,
        string? Suffix,
        string? Nickname,
        string Salutation,
        string Rank,
        int? StartYear,
        int? EndYear,
        string DatesUserEntry,
        string NameUserEntry,
        string ConflictsServed,
        string Awards,
        string PhoneNumber,
        string EmailAddress,
        int? ServiceBranchCategoryId,
        int? ServiceBranchId,
        string? FlagLocation,
        bool IsActive,
        string PhotoFileName,
        int? LegacyKey,
        string CreatedBy,
        DateTime CreatedDate,
        string ModifiedBy,
        DateTime ModifiedDate,
        string? DeletedBy,
        DateTime? DeletedDate);

    private sealed record GridSnap(int Id, int? HonoreeId, string ModifiedBy, DateTime ModifiedDate, string Name, DateTime? DeletedDate);

    private sealed record FlagClaimSnap(
        int Id,
        int? HonoreeId,
        int? FlagGridId,
        string ExternalUserObjectId,
        string ExternalUserEmail,
        string? ExternalUserName,
        string ClaimStatus);

    private sealed record HonoreeClaimSnap(
        int Id,
        int HonoreeId,
        string UserId,
        DateTime ClaimedUtc,
        string Email,
        string? DisplayName,
        string ClaimStatus,
        int? SourceFlagClaimId);

    private sealed record ChangeSnap(int Id, int? HonoreeId, int? FlagGridId);
}
