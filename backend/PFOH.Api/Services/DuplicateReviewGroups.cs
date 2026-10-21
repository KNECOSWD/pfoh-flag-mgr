using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

/// <summary>
/// Open likely-duplicate groups: active records only, with pairs in
/// dbo.HonoreeNotDuplicatePairs left out. Read-only. The admin bar count and
/// the duplicate review list both use <see cref="Match"/>.
/// </summary>
public static class DuplicateReviewGroups
{
    public const string OpenGroupCountHeader = "X-Open-Duplicate-Groups";

    public static async Task<int> CountOpenAsync(PfohDbContext db, CancellationToken ct)
    {
        var honorees = await LoadActiveAsync(db, ct);
        var suppressed = await LoadSuppressedAsync(db, ct);
        return Match(honorees, suppressed).Count;
    }

    public static async Task<List<Honoree>> LoadActiveAsync(PfohDbContext db, CancellationToken ct) =>
        await db.Honorees
            .AsNoTracking()
            .Include(honoree => honoree.FlagGrid)
            .Include(honoree => honoree.ServiceBranch)
            .Include(honoree => honoree.Sponsor)
            .Where(honoree => honoree.IsActive && honoree.DeletedDate == null)
            .OrderBy(honoree => honoree.LastName)
            .ThenBy(honoree => honoree.FirstName)
            .ThenBy(honoree => honoree.Id)
            .ToListAsync(ct);

    public static async Task<List<(int Low, int High)>> LoadSuppressedAsync(PfohDbContext db, CancellationToken ct)
    {
        var rows = await db.HonoreeNotDuplicatePairs
            .AsNoTracking()
            .Select(pair => new { pair.LowHonoreeId, pair.HighHonoreeId })
            .ToListAsync(ct);
        return rows.Select(pair => (pair.LowHonoreeId, pair.HighHonoreeId)).ToList();
    }

    public static IReadOnlyList<DuplicateMatchGroup> Match(
        IReadOnlyList<Honoree> honorees,
        IEnumerable<(int Low, int High)> suppressed)
    {
        var candidates = honorees.Select(honoree => new DuplicateCandidate(
            honoree.Id,
            DisplayName(honoree),
            honoree.ServiceBranch?.ServiceBranchName,
            honoree.FlagGrid?.FlagGridName,
            SponsorName(honoree.Sponsor),
            honoree.Sponsor?.EmailAddress,
            honoree.IsActive,
            honoree.DeletedDate)).ToList();
        return HonoreeDuplicateMatcher.Match(candidates, suppressed);
    }

    public static string DisplayName(Honoree honoree)
    {
        var name = string.Join(
            " ",
            new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (string.IsNullOrWhiteSpace(honoree.Nickname))
        {
            return name;
        }

        return name.EndsWith($"({honoree.Nickname})", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} ({honoree.Nickname})";
    }

    public static string SponsorName(Sponsor? sponsor)
    {
        if (sponsor is null)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            new[] { sponsor.FirstName, sponsor.MiddleName, sponsor.LastName, sponsor.Suffix }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}
