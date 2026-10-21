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
        CancellationToken ct)
    {
        var byId = confirmed.ToDictionary(honoree => honoree.Id);
        var now = DateTime.UtcNow;
        custom ??= new MergeCustom();

        survivor.FirstName = ReadText(byId, fields, custom, "firstName", honoree => honoree.FirstName);
        survivor.MiddleName = ReadOptional(byId, fields, custom, "middleName", honoree => honoree.MiddleName);
        survivor.LastName = ReadText(byId, fields, custom, "lastName", honoree => honoree.LastName);
        survivor.Suffix = ReadOptional(byId, fields, custom, "suffix", honoree => honoree.Suffix);
        survivor.Nickname = ReadOptional(byId, fields, custom, "nickname", honoree => honoree.Nickname);
        survivor.Rank = ReadText(byId, fields, custom, "rank", honoree => honoree.Rank);
        survivor.ConflictsServed = ReadText(byId, fields, custom, "conflictsServed", honoree => honoree.ConflictsServed);
        survivor.Awards = Text(byId, fields, "awards", honoree => honoree.Awards);
        survivor.Description = ReadText(byId, fields, custom, "description", honoree => honoree.Description);
        survivor.KIA = custom.HasKia ? custom.Kia : byId[fields["kia"]].KIA;
        survivor.PhotoFileName = Text(byId, fields, "photo", honoree => honoree.PhotoFileName);
        survivor.PhoneNumber = ReadText(byId, fields, custom, "phone", honoree => honoree.PhoneNumber);
        survivor.EmailAddress = ReadText(byId, fields, custom, "email", honoree => honoree.EmailAddress);
        survivor.SponsorId = custom.HasSponsor ? custom.SponsorId : byId[fields["sponsor"]].SponsorId;

        if (custom.HasBranch)
        {
            survivor.ServiceBranchId = custom.BranchId;
            survivor.ServiceBranchCategoryId = custom.BranchCategoryId;
        }
        else
        {
            var branch = byId[fields["serviceBranch"]];
            survivor.ServiceBranchId = branch.ServiceBranchId;
            survivor.ServiceBranchCategoryId = branch.ServiceBranchCategoryId;
        }

        if (custom.HasYears)
        {
            survivor.StartYear = custom.StartYear;
            survivor.EndYear = custom.EndYear;
            survivor.DatesUserEntry = custom.DatesUserEntry ?? string.Empty;
        }
        else
        {
            var years = byId[fields["serviceYears"]];
            survivor.StartYear = years.StartYear;
            survivor.EndYear = years.EndYear;
            survivor.DatesUserEntry = years.DatesUserEntry ?? string.Empty;
        }

        var keptGridId = custom.HasGrid ? custom.FlagGridId : byId[fields["flagGrid"]].FlagGridId;
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

        var released = new HashSet<int>();
        foreach (var other in confirmed.Where(honoree => honoree.Id != survivor.Id))
        {
            if (other.FlagGridId is int otherGridId && otherGridId != keptGridId && grids.TryGetValue(otherGridId, out var otherGrid))
            {
                FlagSpotRelease.ClearAssignment(other, otherGrid, adminName, now);
                released.Add(otherGridId);
            }
            else
            {
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
            if (!seenSpot.Add((claim.FlagGridId!.Value, claim.ExternalUserObjectId)))
            {
                claim.FlagGridId = null;
            }
        }

        await db.SaveChangesAsync(ct);
        await HonoreeClaimStore.SyncAsync(db, ct);
        var claimants = await HonoreeClaimStore.ActiveForHonoreeAsync(db, survivor.Id, ct);
        return new HonoreeMergeResult(survivor.Id, deactivatedIds, claimants);
    }

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
