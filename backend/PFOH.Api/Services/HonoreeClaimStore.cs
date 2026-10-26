using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

/// <summary>
/// Keeps dbo.HonoreeClaims aligned with person claims on dbo.FlagClaims.
/// One row per honoree and person. Synthetic reprint claims are not claimants.
/// </summary>
public static class HonoreeClaimStore
{
    public static readonly string[] HiddenStatuses = ["Unclaimed", "Cancelled", "Unlinked", "AdminDirectEditCompleted"];

    public static bool IsPerson(string? userId) =>
        !string.IsNullOrWhiteSpace(userId) &&
        !userId.StartsWith("admin-reprint:", StringComparison.Ordinal);

    public static async Task SyncAsync(PfohDbContext db, CancellationToken ct)
    {
        var claims = await db.FlagClaims
            .Where(claim => claim.HonoreeId != null)
            .ToListAsync(ct);

        var active = claims
            .Where(claim => IsPerson(claim.ExternalUserObjectId) && !HiddenStatuses.Contains(claim.ClaimStatus))
            .GroupBy(claim => (claim.HonoreeId!.Value, claim.ExternalUserObjectId))
            .ToDictionary(group => group.Key, group => group.ToList());

        var existing = await db.HonoreeClaims.ToListAsync(ct);
        var seen = new HashSet<(int HonoreeId, string UserId)>();

        foreach (var row in existing)
        {
            var key = (row.HonoreeId, row.UserId);
            seen.Add(key);
            if (!active.TryGetValue(key, out var group))
            {
                if (!HiddenStatuses.Contains(row.ClaimStatus))
                {
                    row.ClaimStatus = "Unclaimed";
                }

                continue;
            }

            Copy(row, group);
        }

        foreach (var (key, group) in active)
        {
            if (seen.Contains(key))
            {
                continue;
            }

            var row = new HonoreeClaim
            {
                HonoreeId = key.Item1,
                UserId = key.Item2
            };
            Copy(row, group);
            db.HonoreeClaims.Add(row);
        }

        await db.SaveChangesAsync(ct);
    }

    public static async Task<IReadOnlyList<HonoreeClaim>> ActiveForHonoreeAsync(
        PfohDbContext db,
        int honoreeId,
        CancellationToken ct) =>
        await db.HonoreeClaims
            .AsNoTracking()
            .Where(claim => claim.HonoreeId == honoreeId && !HiddenStatuses.Contains(claim.ClaimStatus))
            .OrderBy(claim => claim.DisplayName ?? claim.Email)
            .ThenBy(claim => claim.UserId)
            .ToListAsync(ct);

    private static void Copy(HonoreeClaim row, List<FlagClaim> group)
    {
        var latest = group
            .OrderByDescending(claim => claim.CreatedUtc)
            .ThenByDescending(claim => claim.Id)
            .First();
        var earliest = group
            .OrderBy(claim => claim.CreatedUtc)
            .ThenBy(claim => claim.Id)
            .First();
        row.ClaimedUtc = earliest.CreatedUtc;
        row.Email = latest.ExternalUserEmail ?? string.Empty;
        row.DisplayName = latest.ExternalUserName;
        row.ClaimStatus = latest.ClaimStatus;
        row.SourceFlagClaimId = latest.Id;
    }
}
