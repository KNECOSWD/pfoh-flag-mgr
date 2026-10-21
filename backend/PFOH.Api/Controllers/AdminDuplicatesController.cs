using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Extensions;
using PFOH.Api.Models;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/duplicates")]
[Authorize(Policy = "AdminOnly")]
[EnableRateLimiting("AuthenticatedGet")]
public class AdminDuplicatesController(PfohDbContext db) : ControllerBase
{
    [HttpGet("groups")]
    public async Task<ActionResult<IReadOnlyList<DuplicateGroupDto>>> Groups(CancellationToken ct)
    {
        var groups = await BuildGroupsAsync(ct);
        return Ok(groups);
    }

    [HttpPost("merge")]
    public async Task<ActionResult<DuplicateMergeResultDto>> Merge(
        [FromBody] DuplicateMergeRequest request,
        CancellationToken ct)
    {
        var ids = (request.DuplicateIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();
        if (ids.Count < 2)
        {
            return BadRequest(new { message = "Confirm at least two records before merging." });
        }

        if (request.SurvivorId <= 0 || !ids.Contains(request.SurvivorId))
        {
            return BadRequest(new { message = "Choose the surviving record from the confirmed duplicates. Nothing is selected automatically." });
        }

        var fields = request.Fields ?? new Dictionary<string, int>();
        var missing = DuplicateMergeFields.Keys.Where(key => !fields.ContainsKey(key)).ToList();
        if (missing.Count > 0)
        {
            return BadRequest(new { message = "Choose a value for every field before merging." });
        }

        if (fields.Values.Any(id => !ids.Contains(id)))
        {
            return BadRequest(new { message = "Each kept value has to come from one of the confirmed records." });
        }

        var honorees = await db.Honorees
            .Where(honoree => ids.Contains(honoree.Id))
            .ToListAsync(ct);
        if (honorees.Count != ids.Count || honorees.Any(honoree => !honoree.IsActive || honoree.DeletedDate != null))
        {
            return Conflict(new { message = "One of the confirmed records is inactive or deleted. It was not merged." });
        }

        var groups = await BuildGroupsAsync(ct);
        var group = groups.FirstOrDefault(item => ids.All(id => item.Honorees.Any(honoree => honoree.Id == id)));
        if (group is null)
        {
            return BadRequest(new { message = "Confirm records from the same duplicate group. Records that are not in that group are left unchanged." });
        }

        var survivor = honorees.Single(honoree => honoree.Id == request.SurvivorId);
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var before = await db.Honorees.CountAsync(ct);
            var result = await HonoreeMerge.ApplyAsync(db, survivor, honorees, fields, adminName, ct);
            var after = await db.Honorees.CountAsync(ct);
            if (after != before)
            {
                await transaction.RollbackAsync(ct);
                return (ActionResult<DuplicateMergeResultDto>)StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { message = "The merge stopped because it would have created or removed a record." });
            }

            await transaction.CommitAsync(ct);
            return new DuplicateMergeResultDto(
                result.SurvivorId,
                DisplayName(survivor),
                result.DeactivatedIds,
                result.Claimants.Select(ToClaimant).ToList());
        });
    }

    [HttpPost("not-duplicates")]
    public async Task<ActionResult<NotDuplicatePairDto>> MarkNotDuplicates(
        [FromBody] NotDuplicatePairRequest request,
        CancellationToken ct)
    {
        if (request.LeftId <= 0 || request.RightId <= 0 || request.LeftId == request.RightId)
        {
            return BadRequest(new { message = "Choose two different records." });
        }

        var low = Math.Min(request.LeftId, request.RightId);
        var high = Math.Max(request.LeftId, request.RightId);
        var existing = await db.HonoreeNotDuplicatePairs
            .FirstOrDefaultAsync(pair => pair.LowHonoreeId == low && pair.HighHonoreeId == high, ct);
        if (existing is not null)
        {
            return Ok(ToPair(existing));
        }

        var found = await db.Honorees
            .Where(honoree => honoree.Id == low || honoree.Id == high)
            .Select(honoree => honoree.Id)
            .ToListAsync(ct);
        if (found.Count != 2)
        {
            return BadRequest(new { message = "Both records have to exist before they can be marked as not duplicates." });
        }

        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var row = new HonoreeNotDuplicatePair
        {
            LowHonoreeId = low,
            HighHonoreeId = high,
            MarkedBy = adminName,
            MarkedUtc = DateTime.UtcNow
        };
        db.HonoreeNotDuplicatePairs.Add(row);
        await db.SaveChangesAsync(ct);
        return Ok(ToPair(row));
    }

    private static NotDuplicatePairDto ToPair(HonoreeNotDuplicatePair pair) =>
        new(pair.LowHonoreeId, pair.HighHonoreeId, pair.MarkedBy, pair.MarkedUtc);

    private async Task<IReadOnlyList<DuplicateGroupDto>> BuildGroupsAsync(CancellationToken ct)
    {
        await HonoreeClaimStore.SyncAsync(db, ct);

        var honorees = await db.Honorees
            .AsNoTracking()
            .Include(honoree => honoree.FlagGrid)
            .Include(honoree => honoree.ServiceBranch)
            .Include(honoree => honoree.Sponsor)
            .Where(honoree => honoree.IsActive && honoree.DeletedDate == null)
            .OrderBy(honoree => honoree.LastName)
            .ThenBy(honoree => honoree.FirstName)
            .ThenBy(honoree => honoree.Id)
            .ToListAsync(ct);

        var ids = honorees.Select(honoree => honoree.Id).ToList();
        var claims = ids.Count == 0
            ? new List<HonoreeClaim>()
            : await db.HonoreeClaims
                .AsNoTracking()
                .Where(claim => ids.Contains(claim.HonoreeId) && !HonoreeClaimStore.HiddenStatuses.Contains(claim.ClaimStatus))
                .ToListAsync(ct);
        var claimsByHonoree = claims.GroupBy(claim => claim.HonoreeId).ToDictionary(group => group.Key, group => group.ToList());

        var candidates = honorees.Select(honoree => new DuplicateCandidate(
            honoree.Id,
            DisplayName(honoree),
            honoree.ServiceBranch?.ServiceBranchName,
            honoree.FlagGrid?.FlagGridName,
            SponsorName(honoree.Sponsor),
            honoree.Sponsor?.EmailAddress,
            honoree.IsActive,
            honoree.DeletedDate)).ToList();

        var suppressed = await db.HonoreeNotDuplicatePairs
            .AsNoTracking()
            .Select(pair => new { pair.LowHonoreeId, pair.HighHonoreeId })
            .ToListAsync(ct);

        var byId = honorees.ToDictionary(honoree => honoree.Id);
        return HonoreeDuplicateMatcher.Match(
            candidates,
            suppressed.Select(pair => (pair.LowHonoreeId, pair.HighHonoreeId)))
            .Select(group =>
            {
                var members = group.Ids.Select(id => ToHonoree(byId[id], claimsByHonoree)).ToList();
                var pairs = group.Pairs
                    .Select(pair => new DuplicatePairDto(pair.LeftId, pair.RightId, pair.ReasonCodes, pair.Explanation))
                    .ToList();
                return new DuplicateGroupDto(
                    string.Join('-', group.Ids),
                    group.Likelihood,
                    group.Explanation,
                    group.ReasonCodes,
                    pairs,
                    members);
            })
            .ToList();
    }

    private static DuplicateHonoreeDto ToHonoree(
        Honoree honoree,
        IReadOnlyDictionary<int, List<HonoreeClaim>> claimsByHonoree)
    {
        claimsByHonoree.TryGetValue(honoree.Id, out var claims);
        claims ??= new List<HonoreeClaim>();
        return new DuplicateHonoreeDto(
            honoree.Id,
            DisplayName(honoree),
            honoree.FirstName,
            honoree.MiddleName,
            honoree.LastName,
            honoree.Suffix,
            honoree.Nickname,
            honoree.Rank,
            honoree.ServiceBranchId,
            honoree.ServiceBranchCategoryId,
            honoree.ServiceBranch?.ServiceBranchName,
            honoree.StartYear,
            honoree.EndYear,
            honoree.DatesUserEntry,
            ServiceYears(honoree),
            honoree.ConflictsServed,
            honoree.Awards,
            honoree.Description,
            honoree.KIA,
            honoree.PhotoFileName,
            string.IsNullOrWhiteSpace(honoree.PhotoFileName) ? null : $"/api/honorees/{honoree.Id}/photo",
            honoree.FlagGridId,
            honoree.FlagGrid?.FlagGridName,
            honoree.PhoneNumber,
            honoree.EmailAddress,
            honoree.SponsorId,
            SponsorName(honoree.Sponsor),
            claims.OrderBy(claim => claim.DisplayName ?? claim.Email).Select(ToClaimant).ToList());
    }

    private static DuplicateClaimantDto ToClaimant(HonoreeClaim claim) =>
        new(claim.UserId, claim.DisplayName, claim.Email, claim.ClaimedUtc, claim.ClaimStatus);

    internal static string DisplayName(Honoree honoree)
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

    private static string SponsorName(Sponsor? sponsor)
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

    private static string ServiceYears(Honoree honoree)
    {
        if (!string.IsNullOrWhiteSpace(honoree.DatesUserEntry))
        {
            return honoree.DatesUserEntry;
        }

        if (honoree.StartYear.HasValue && honoree.EndYear.HasValue)
        {
            return $"{honoree.StartYear} - {honoree.EndYear}";
        }

        if (honoree.StartYear.HasValue)
        {
            return $"{honoree.StartYear} - Present";
        }

        return honoree.EndYear.HasValue ? $"- {honoree.EndYear}" : string.Empty;
    }
}

public sealed class DuplicateMergeRequest
{
    public int SurvivorId { get; set; }

    public List<int> DuplicateIds { get; set; } = new();

    public Dictionary<string, int> Fields { get; set; } = new();
}

public sealed class NotDuplicatePairRequest
{
    public int LeftId { get; set; }

    public int RightId { get; set; }
}

public sealed record NotDuplicatePairDto(
    int LowHonoreeId,
    int HighHonoreeId,
    string MarkedBy,
    DateTime MarkedUtc);

public sealed record DuplicateGroupDto(
    string GroupKey,
    string Likelihood,
    string Explanation,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<DuplicatePairDto> Pairs,
    IReadOnlyList<DuplicateHonoreeDto> Honorees);

public sealed record DuplicatePairDto(
    int LeftId,
    int RightId,
    IReadOnlyList<string> ReasonCodes,
    string Explanation);

public sealed record DuplicateClaimantDto(
    string UserId,
    string? Name,
    string Email,
    DateTime ClaimedUtc,
    string ClaimStatus);

public sealed record DuplicateHonoreeDto(
    int Id,
    string Name,
    string FirstName,
    string? MiddleName,
    string LastName,
    string? Suffix,
    string? Nickname,
    string Rank,
    int? ServiceBranchId,
    int? ServiceBranchCategoryId,
    string? ServiceBranchName,
    int? StartYear,
    int? EndYear,
    string DatesUserEntry,
    string ServiceYears,
    string ConflictsServed,
    string Awards,
    string Description,
    bool Kia,
    string PhotoFileName,
    string? PhotoUrl,
    int? FlagGridId,
    string? FlagGridName,
    string PhoneNumber,
    string EmailAddress,
    int? SponsorId,
    string SponsorName,
    IReadOnlyList<DuplicateClaimantDto> Claimants);

public sealed record DuplicateMergeResultDto(
    int SurvivorId,
    string SurvivorName,
    IReadOnlyList<int> DeactivatedIds,
    IReadOnlyList<DuplicateClaimantDto> Claimants);
