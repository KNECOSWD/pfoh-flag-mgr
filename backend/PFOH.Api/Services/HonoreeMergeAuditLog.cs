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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<HonoreeMergeAudit> CaptureAsync(
        PfohDbContext db,
        Honoree survivor,
        IReadOnlyList<Honoree> confirmed,
        IReadOnlyDictionary<string, int> fields,
        MergeCustom? custom,
        string adminName,
        DateTime now,
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
            grids.Select(grid => new GridSnap(grid.Id, grid.HonoreeId, grid.ModifiedBy, grid.ModifiedDate)).ToList(),
            flagClaims.Select(claim => new FlagClaimSnap(claim.Id, claim.HonoreeId, claim.FlagGridId)).ToList(),
            honoreeClaims.Select(CopyClaim).ToList(),
            changes.Select(change => new ChangeSnap(change.Id, change.HonoreeId, change.FlagGridId)).ToList());

        var absorbed = confirmed.Where(honoree => honoree.Id != survivor.Id).Select(honoree => honoree.Id).ToList();
        var audit = new HonoreeMergeAudit
        {
            SurvivorId = survivor.Id,
            AbsorbedHonoreeIds = JsonSerializer.Serialize(absorbed, Json),
            BeforeState = JsonSerializer.Serialize(before, Json),
            AfterSurvivorState = "{}",
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
        var after = JsonSerializer.Deserialize<HonoreeSnap>(audit.AfterSurvivorState, Json);
        if (before is null || after is null)
        {
            return SurvivorEdited;
        }

        var survivor = await db.Honorees.FirstOrDefaultAsync(honoree => honoree.Id == audit.SurvivorId, ct);
        if (survivor is null || survivor.ModifiedDate != audit.SurvivorModifiedUtc || !SameSurvivor(survivor, after))
        {
            return SurvivorEdited;
        }

        var honoreeIds = before.Honorees.Select(honoree => honoree.Id).ToList();
        var honorees = await db.Honorees.Where(honoree => honoreeIds.Contains(honoree.Id)).ToListAsync(ct);
        if (honorees.Count != honoreeIds.Count)
        {
            return SurvivorEdited;
        }

        var gridIds = before.Grids.Select(grid => grid.Id).ToList();
        var grids = gridIds.Count == 0
            ? new List<FlagGrid>()
            : await db.FlagGrids.Where(grid => gridIds.Contains(grid.Id)).ToListAsync(ct);
        var claimIds = before.FlagClaims.Select(claim => claim.Id).ToList();
        var flagClaims = claimIds.Count == 0
            ? new List<FlagClaim>()
            : await db.FlagClaims.Where(claim => claimIds.Contains(claim.Id)).ToListAsync(ct);
        var honoreeClaimIds = before.HonoreeClaims.Select(claim => claim.Id).ToList();
        var honoreeClaims = honoreeClaimIds.Count == 0
            ? new List<HonoreeClaim>()
            : await db.HonoreeClaims.Where(claim => honoreeClaimIds.Contains(claim.Id)).ToListAsync(ct);
        var changeIds = before.ChangeRequests.Select(change => change.Id).ToList();
        var changes = changeIds.Count == 0
            ? new List<HonoreeChangeRequest>()
            : await db.HonoreeChangeRequests.Where(change => changeIds.Contains(change.Id)).ToListAsync(ct);
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

        audit.UndoneBy = TrimAdmin(adminName);
        audit.UndoneUtc = DateTime.UtcNow;
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

    private sealed record GridSnap(int Id, int? HonoreeId, string ModifiedBy, DateTime ModifiedDate);

    private sealed record FlagClaimSnap(int Id, int? HonoreeId, int? FlagGridId);

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
