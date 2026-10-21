using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
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
public class AdminDuplicatesController(
    PfohDbContext db,
    IWebHostEnvironment environment,
    HonoreeFileStorage fileStorage) : ControllerBase
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
        var prepared = await PrepareMergeAsync(request, track: true, ct);
        if (prepared.Error is not null)
        {
            return new ActionResult<DuplicateMergeResultDto>(prepared.Error);
        }

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
            var result = await HonoreeMerge.ApplyAsync(
                db,
                prepared.Survivor,
                prepared.Ordered,
                prepared.Fields,
                prepared.Custom,
                adminName,
                ct);
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
                DisplayName(prepared.Survivor),
                result.DeactivatedIds,
                result.Claimants.Select(ToClaimant).ToList());
        });
    }

    /// <summary>
    /// Renders the card the merge would save. Nothing is written: no honoree
    /// update, no print or reprint queue row, and no file upload.
    /// </summary>
    [HttpPost("merge-preview/card")]
    public async Task<IActionResult> PreviewCard(
        [FromBody] DuplicateMergeRequest request,
        CancellationToken ct)
    {
        var prepared = await PrepareMergeAsync(request, track: false, ct);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var preview = new Honoree { IsActive = true };
        HonoreeMerge.ApplyValues(preview, prepared.Ordered, prepared.Fields, prepared.Custom);
        var byId = prepared.Ordered.ToDictionary(honoree => honoree.Id);
        preview.FlagGridId = HonoreeMerge.KeptGridId(byId, prepared.Fields, prepared.Custom);
        if (preview.ServiceBranchId is int branchId)
        {
            preview.ServiceBranch = prepared.Ordered
                .Select(honoree => honoree.ServiceBranch)
                .FirstOrDefault(branch => branch?.Id == branchId)
                ?? await db.ServiceBranches.AsNoTracking().FirstOrDefaultAsync(branch => branch.Id == branchId, ct);
        }

        if (preview.FlagGridId is int gridId)
        {
            preview.FlagGrid = prepared.Ordered
                .Select(honoree => honoree.FlagGrid)
                .FirstOrDefault(grid => grid?.Id == gridId)
                ?? await db.FlagGrids.AsNoTracking().FirstOrDefaultAsync(grid => grid.Id == gridId, ct);
        }

        var change = new HonoreeChangeRequest
        {
            FirstName = preview.FirstName,
            MiddleName = preview.MiddleName,
            LastName = preview.LastName,
            Suffix = preview.Suffix,
            Nickname = preview.Nickname,
            Rank = preview.Rank,
            ServiceBranchId = preview.ServiceBranchId,
            ServiceBranchCategoryId = preview.ServiceBranchCategoryId,
            ServiceBranch = preview.ServiceBranch,
            StartYear = preview.StartYear,
            EndYear = preview.EndYear,
            DatesUserEntry = preview.DatesUserEntry,
            ConflictsServed = preview.ConflictsServed,
            Awards = preview.Awards,
            Description = preview.Description,
            KIA = preview.KIA,
            PhotoFileName = preview.PhotoFileName,
            FlagGridId = preview.FlagGridId,
            FlagGrid = preview.FlagGrid
        };
        var honoree = HonoreeCardPdf.FromChangeRequest(change);
        var pdf = await HonoreeCardPdf.RenderAsync(
            environment,
            fileStorage,
            honoree,
            null,
            change.PhotoFileName,
            null,
            ct);
        return File(pdf, "application/pdf", "duplicate-merge-preview.pdf");
    }

    private async Task<MergePreparation> PrepareMergeAsync(
        DuplicateMergeRequest request,
        bool track,
        CancellationToken ct)
    {
        var ids = (request.DuplicateIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();
        if (ids.Count < 2)
        {
            return MergePreparation.Fail(BadRequest(new { message = "Confirm at least two records before merging." }));
        }

        var fields = request.Fields ?? new Dictionary<string, int>();
        var values = request.Values ?? new Dictionary<string, string?>();
        var shapeError = HonoreeMergeValues.ValidateShape(fields, values, ids, request.CombineAwards);
        if (shapeError is not null)
        {
            return MergePreparation.Fail(BadRequest(new { message = shapeError }));
        }

        var survivorError = HonoreeMergeValues.SurvivorError(request.SurvivorId, fields["flagGrid"]);
        if (survivorError is not null)
        {
            return MergePreparation.Fail(BadRequest(new { message = survivorError }));
        }

        IQueryable<Honoree> query = db.Honorees
            .Include(honoree => honoree.FlagGrid)
            .Include(honoree => honoree.ServiceBranch)
            .Where(honoree => ids.Contains(honoree.Id));
        if (!track)
        {
            query = query.AsNoTracking();
        }

        var honorees = await query.ToListAsync(ct);
        if (honorees.Count != ids.Count || honorees.Any(honoree => !honoree.IsActive || honoree.DeletedDate != null))
        {
            return MergePreparation.Fail(Conflict(new { message = "One of the confirmed records is inactive or deleted. It was not merged." }));
        }

        var active = await DuplicateReviewGroups.LoadActiveAsync(db, ct);
        var suppressed = await DuplicateReviewGroups.LoadSuppressedAsync(db, ct);
        var matched = DuplicateReviewGroups.Match(active, suppressed);
        if (!matched.Any(group => ids.All(id => group.Ids.Contains(id))))
        {
            return MergePreparation.Fail(BadRequest(new { message = "Confirm records from the same duplicate group. Records that are not in that group are left unchanged." }));
        }

        var resolved = await HonoreeMergeValues.ResolveAsync(db, fields, values, ids, ct);
        if (resolved.Error is not null || resolved.Custom is null)
        {
            return MergePreparation.Fail(BadRequest(new { message = resolved.Error ?? "Choose a value for every field before merging." }));
        }

        resolved.Custom.CombineAwards = request.CombineAwards && !resolved.Custom.Text.ContainsKey("awards");
        var ordered = ids.Select(id => honorees.Single(honoree => honoree.Id == id)).ToList();
        var survivor = ordered.Single(honoree => honoree.Id == fields["flagGrid"]);
        return new MergePreparation(ordered, survivor, fields, resolved.Custom);
    }

    private sealed class MergePreparation
    {
        public MergePreparation(
            List<Honoree> ordered,
            Honoree survivor,
            Dictionary<string, int> fields,
            MergeCustom custom)
        {
            Ordered = ordered;
            Survivor = survivor;
            Fields = fields;
            Custom = custom;
        }

        public ActionResult? Error { get; private init; }

        public List<Honoree> Ordered { get; }

        public Honoree Survivor { get; }

        public Dictionary<string, int> Fields { get; }

        public MergeCustom Custom { get; }

        public static MergePreparation Fail(ActionResult error) =>
            new(new List<Honoree>(), new Honoree(), new Dictionary<string, int>(), new MergeCustom())
            {
                Error = error
            };
    }

    [HttpGet("merges")]
    public async Task<ActionResult<IReadOnlyList<MergeHistoryRecord>>> Merges(CancellationToken ct)
    {
        return Ok(await HonoreeMergeAuditLog.ListAsync(db, ct));
    }

    [HttpPost("merges/{id:int}/undo")]
    public async Task<IActionResult> UndoMerge(int id, CancellationToken ct)
    {
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var error = await HonoreeMergeAuditLog.UndoAsync(db, id, adminName, ct);
            if (error == "missing")
            {
                await transaction.RollbackAsync(ct);
                return NotFound(new { message = "That merge was not found." });
            }

            if (error is not null)
            {
                await transaction.RollbackAsync(ct);
                return Conflict(new { message = error });
            }

            await transaction.CommitAsync(ct);
            return Ok(new { id });
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

        var honorees = await DuplicateReviewGroups.LoadActiveAsync(db, ct);

        var ids = honorees.Select(honoree => honoree.Id).ToList();
        var claims = ids.Count == 0
            ? new List<HonoreeClaim>()
            : await db.HonoreeClaims
                .AsNoTracking()
                .Where(claim => ids.Contains(claim.HonoreeId) && !HonoreeClaimStore.HiddenStatuses.Contains(claim.ClaimStatus))
                .ToListAsync(ct);
        var claimsByHonoree = claims.GroupBy(claim => claim.HonoreeId).ToDictionary(group => group.Key, group => group.ToList());

        var suppressed = await DuplicateReviewGroups.LoadSuppressedAsync(db, ct);
        var byId = honorees.ToDictionary(honoree => honoree.Id);
        return DuplicateReviewGroups.Match(honorees, suppressed)
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

    internal static string DisplayName(Honoree honoree) => DuplicateReviewGroups.DisplayName(honoree);

    private static string SponsorName(Sponsor? sponsor) => DuplicateReviewGroups.SponsorName(sponsor);

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

    /// <summary>
    /// Typed values keyed by the same field names as <see cref="Fields"/>.
    /// A key here is saved instead of the record named in <see cref="Fields"/>.
    /// An empty photo value means no photo. Any other photo text is rejected.
    /// Awards text is an edit and overrides a record choice or <see cref="CombineAwards"/>.
    /// </summary>
    public Dictionary<string, string?>? Values { get; set; }

    /// <summary>
    /// Join the included records' award lists. An awards entry in <see cref="Values"/> wins.
    /// </summary>
    public bool CombineAwards { get; set; }
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
