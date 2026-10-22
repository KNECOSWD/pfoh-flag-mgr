using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public static class DuplicateMergeFields
{
    public static readonly string[] Keys =
    [
        "firstName",
        "middleName",
        "lastName",
        "suffix",
        "nickname",
        "rank",
        "serviceBranch",
        "serviceYears",
        "conflictsServed",
        "awards",
        "description",
        "kia",
        "photo",
        "flagGrid",
        "phone",
        "email",
        "sponsor"
    ];
}

public sealed record HonoreeMergeResult(
    int SurvivorId,
    IReadOnlyList<int> DeactivatedIds,
    IReadOnlyList<HonoreeClaim> Claimants);

/// <summary>
/// Writes chosen values onto one existing honoree. Other confirmed records are
/// set inactive and their flag spots are cleared with <see cref="FlagSpotRelease"/>.
/// Claim rows move onto the survivor. No new honoree is inserted.
/// </summary>
public static class HonoreeMerge
{
    private static readonly string[] SpotHoldingStatuses = ["Claimed", "Submitted", "Approved"];

    public static async Task<HonoreeMergeResult> ApplyAsync(
        PfohDbContext db,
        Honoree survivor,
        IReadOnlyList<Honoree> confirmed,
        IReadOnlyDictionary<string, int> fields,
        MergeCustom? custom,
        string adminName,
        Guid clientRequestId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        custom ??= new MergeCustom();
        var byId = confirmed.ToDictionary(honoree => honoree.Id);
        var audit = await HonoreeMergeAuditLog.CaptureAsync(
            db, survivor, confirmed, fields, custom, adminName, now, clientRequestId, ct);
        ApplyValues(survivor, confirmed, fields, custom);
        var keptGridId = KeptGridId(byId, fields, custom);
        var gridIds = confirmed
            .Select(honoree => honoree.FlagGridId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Append(survivor.FlagGridId ?? -1)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (keptGridId is int kept && !gridIds.Contains(kept))
        {
            gridIds.Add(kept);
        }

        var grids = gridIds.Count == 0
            ? new Dictionary<int, FlagGrid>()
            : await db.FlagGrids
                .Where(grid => gridIds.Contains(grid.Id))
                .ToDictionaryAsync(grid => grid.Id, ct);

        var survivorName = FlagRemovalQueue.DisplayName(survivor);
        string? keptSpotName = keptGridId is int keptId && grids.TryGetValue(keptId, out var keptNamed)
            ? keptNamed.FlagGridName
            : null;
        var mergeDetail = keptSpotName is null
            ? $"Spot released when this record was merged into {survivorName}."
            : $"Spot released when this record was merged into {survivorName}, who kept spot {keptSpotName}.";

        var released = new HashSet<int>();
        foreach (var other in confirmed.Where(honoree => honoree.Id != survivor.Id))
        {
            if (other.FlagGridId is int otherGridId && otherGridId != keptGridId && grids.TryGetValue(otherGridId, out var otherGrid))
            {
                FlagSpotRelease.ClearAssignment(other, otherGrid, adminName, now);
                released.Add(otherGridId);
                FlagRemovalQueue.Record(
                    db,
                    other,
                    otherGrid,
                    FlagRemovalQueue.Merge,
                    adminName,
                    now,
                    mergeAudit: audit,
                    sourceDetail: mergeDetail);
            }
            else
            {
                // No grid row to release. A stale FlagGridId is cleared and does not queue a pull.
                other.FlagGridId = null;
                other.ModifiedBy = adminName;
                other.ModifiedDate = now;
            }

            other.IsActive = false;
        }

        if (survivor.FlagGridId is int previousGridId &&
            previousGridId != keptGridId &&
            grids.TryGetValue(previousGridId, out var previousGrid))
        {
            FlagSpotRelease.ClearAssignment(survivor, previousGrid, adminName, now);
            released.Add(previousGridId);
            FlagRemovalQueue.Record(
                db,
                survivor,
                previousGrid,
                FlagRemovalQueue.Merge,
                adminName,
                now,
                mergeAudit: audit,
                sourceDetail: mergeDetail);
        }

        if (keptGridId is int keep && grids.TryGetValue(keep, out var keptGrid))
        {
            survivor.FlagGridId = keep;
            keptGrid.HonoreeId = survivor.Id;
            keptGrid.ModifiedBy = adminName;
            keptGrid.ModifiedDate = now;
        }
        else
        {
            survivor.FlagGridId = null;
        }

        survivor.ModifiedBy = adminName;
        survivor.ModifiedDate = now;
        survivor.IsActive = true;
        HonoreeMergeAuditLog.NoteSurvivorAfter(audit, survivor);

        if (released.Count > 0)
        {
            var held = await db.FlagClaims
                .Where(claim => claim.FlagGridId.HasValue && released.Contains(claim.FlagGridId.Value))
                .ToListAsync(ct);
            foreach (var claim in held)
            {
                claim.FlagGridId = null;
            }
        }

        var deactivatedIds = confirmed.Where(honoree => honoree.Id != survivor.Id).Select(honoree => honoree.Id).ToList();
        var moving = await db.FlagClaims
            .Where(claim => claim.HonoreeId.HasValue && deactivatedIds.Contains(claim.HonoreeId.Value))
            .ToListAsync(ct);
        foreach (var claim in moving)
        {
            if (!HonoreeClaimStore.IsPerson(claim.ExternalUserObjectId))
            {
                continue;
            }

            claim.HonoreeId = survivor.Id;
        }

        var holdingSpot = await db.FlagClaims
            .Where(claim =>
                claim.HonoreeId == survivor.Id &&
                claim.FlagGridId.HasValue &&
                SpotHoldingStatuses.Contains(claim.ClaimStatus))
            .OrderBy(claim => claim.Id)
            .ToListAsync(ct);
        var seenSpot = new HashSet<(int GridId, string UserId)>();
        foreach (var claim in holdingSpot)
        {
            // The grid release above may already have cleared this claim in memory
            // while the query still saw the stored spot.
            if (claim.FlagGridId is not int gridId)
            {
                continue;
            }

            if (!seenSpot.Add((gridId, claim.ExternalUserObjectId)))
            {
                claim.FlagGridId = null;
            }
        }

        await db.SaveChangesAsync(ct);
        await HonoreeClaimStore.SyncAsync(db, ct);
        await HonoreeMergeAuditLog.NoteCreatedClaimsAsync(
            db,
            audit,
            confirmed.Select(honoree => honoree.Id).ToList(),
            ct);
        await HonoreeMergeAuditLog.NoteAfterStateAsync(db, audit, ct);
        var claimants = await HonoreeClaimStore.ActiveForHonoreeAsync(db, survivor.Id, ct);
        return new HonoreeMergeResult(survivor.Id, deactivatedIds, claimants);
    }

    /// <summary>
    /// Writes the normalized field values onto <paramref name="target"/> and
    /// does not touch the database, other records, or the flag grid assignment.
    /// The merge and the card preview both use this.
    /// </summary>
    public static void ApplyValues(
        Honoree target,
        IReadOnlyList<Honoree> confirmed,
        IReadOnlyDictionary<string, int> fields,
        MergeCustom? custom)
    {
        var byId = confirmed.ToDictionary(honoree => honoree.Id);
        custom ??= new MergeCustom();

        target.FirstName = ReadText(byId, fields, custom, "firstName", honoree => honoree.FirstName);
        target.MiddleName = ReadOptional(byId, fields, custom, "middleName", honoree => honoree.MiddleName);
        target.LastName = ReadText(byId, fields, custom, "lastName", honoree => honoree.LastName);
        target.Suffix = ReadOptional(byId, fields, custom, "suffix", honoree => honoree.Suffix);
        target.Nickname = ReadOptional(byId, fields, custom, "nickname", honoree => honoree.Nickname);
        target.Rank = ReadText(byId, fields, custom, "rank", honoree => honoree.Rank);
        target.ConflictsServed = ReadText(byId, fields, custom, "conflictsServed", honoree => honoree.ConflictsServed);
        target.Awards = custom.Text.ContainsKey("awards")
            ? custom.Text["awards"]
            : custom.CombineAwards
                ? HonoreeMergeValues.CombineAwardLists(confirmed.Select(honoree => honoree.Awards))
                : Text(byId, fields, "awards", honoree => honoree.Awards);
        target.Description = ReadText(byId, fields, custom, "description", honoree => honoree.Description);
        target.KIA = custom.HasKia ? custom.Kia : byId[fields["kia"]].KIA;
        target.PhotoFileName = custom.NoPhoto
            ? string.Empty
            : Text(byId, fields, "photo", honoree => honoree.PhotoFileName);
        target.PhoneNumber = ReadText(byId, fields, custom, "phone", honoree => honoree.PhoneNumber);
        target.EmailAddress = ReadText(byId, fields, custom, "email", honoree => honoree.EmailAddress);
        target.SponsorId = custom.HasSponsor ? custom.SponsorId : byId[fields["sponsor"]].SponsorId;

        if (custom.HasBranch)
        {
            target.ServiceBranchId = custom.BranchId;
            target.ServiceBranchCategoryId = custom.BranchCategoryId;
        }
        else
        {
            var branch = byId[fields["serviceBranch"]];
            target.ServiceBranchId = branch.ServiceBranchId;
            target.ServiceBranchCategoryId = branch.ServiceBranchCategoryId;
        }

        if (custom.HasYears)
        {
            target.StartYear = custom.StartYear;
            target.EndYear = custom.EndYear;
            target.DatesUserEntry = custom.DatesUserEntry ?? string.Empty;
        }
        else
        {
            var years = byId[fields["serviceYears"]];
            target.StartYear = years.StartYear;
            target.EndYear = years.EndYear;
            target.DatesUserEntry = years.DatesUserEntry ?? string.Empty;
        }
    }

    public static int? KeptGridId(
        IReadOnlyDictionary<int, Honoree> byId,
        IReadOnlyDictionary<string, int> fields,
        MergeCustom custom) =>
        custom.HasGrid ? custom.FlagGridId : byId[fields["flagGrid"]].FlagGridId;

    private static string ReadText(
        IReadOnlyDictionary<int, Honoree> byId,
        IReadOnlyDictionary<string, int> fields,
        MergeCustom custom,
        string key,
        Func<Honoree, string?> read) =>
        custom.Text.TryGetValue(key, out var typed) ? typed : Text(byId, fields, key, read);

    private static string? ReadOptional(
        IReadOnlyDictionary<int, Honoree> byId,
        IReadOnlyDictionary<string, int> fields,
        MergeCustom custom,
        string key,
        Func<Honoree, string?> read)
    {
        if (custom.Text.TryGetValue(key, out var typed))
        {
            return string.IsNullOrEmpty(typed) ? null : typed;
        }

        return Optional(byId, fields, key, read);
    }

    private static string Text(
        IReadOnlyDictionary<int, Honoree> byId,
        IReadOnlyDictionary<string, int> fields,
        string key,
        Func<Honoree, string?> read) =>
        read(byId[fields[key]]) ?? string.Empty;

    private static string? Optional(
        IReadOnlyDictionary<int, Honoree> byId,
        IReadOnlyDictionary<string, int> fields,
        string key,
        Func<Honoree, string?> read) =>
        read(byId[fields[key]]);
}
