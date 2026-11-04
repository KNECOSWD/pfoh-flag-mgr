using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public static class SubmissionDuplicates
{
    public const string EnabledKey = "SubmissionDuplicates:Enabled";
    public const int ProbeId = 0;
    public static readonly TimeSpan DuplicatePostWindow = TimeSpan.FromSeconds(20);

    public static bool Enabled(IConfiguration configuration)
    {
        var raw = configuration[EnabledKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return !raw.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
            && !raw.Trim().Equals("0", StringComparison.OrdinalIgnoreCase)
            && !raw.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SubmissionProbe(
    string FirstName,
    string? MiddleName,
    string LastName,
    string? Suffix,
    string? Nickname,
    string? Rank,
    int? ServiceBranchId,
    string? SubmitterEmail);

/// <summary>
/// Likely-duplicate check for a submission that has not been saved yet.
/// Candidates go through <see cref="HonoreeDuplicateMatcher"/>; this type only
/// chooses which rows to show and which fields are safe to show.
/// </summary>
public sealed class SubmissionGuardService(
    PfohDbContext db,
    IConfiguration configuration,
    IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector matchTokens = dataProtection.CreateProtector("PFOH.SubmissionMatch.v1");

    public bool Enabled => SubmissionDuplicates.Enabled(configuration);

    public async Task<IReadOnlyList<SubmissionMatchDto>> FindAsync(
        SubmissionProbe probe,
        int? excludeHonoreeId,
        int? excludeChangeRequestId,
        bool callerIsAdmin,
        string? callerObjectId,
        CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(probe.FirstName) || string.IsNullOrWhiteSpace(probe.LastName))
        {
            return [];
        }

        var branches = await db.ServiceBranches
            .AsNoTracking()
            .ToDictionaryAsync(branch => branch.Id, branch => branch.ServiceBranchName, ct);
        var probeBranch = probe.ServiceBranchId is int branchId && branches.TryGetValue(branchId, out var branchName)
            ? branchName
            : null;
        var probeEmail = NormalizeEmail(probe.SubmitterEmail);

        var honorees = await db.Honorees
            .AsNoTracking()
            .Include(honoree => honoree.ServiceBranch)
            .Include(honoree => honoree.FlagGrid)
            .Where(honoree => honoree.IsActive && honoree.DeletedDate == null)
            .ToListAsync(ct);

        var pending = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(request => request.ServiceBranch)
            .Include(request => request.FlagClaim)
            .Where(request => request.RequestStatus == "Submitted")
            .ToListAsync(ct);

        var candidates = new List<DuplicateCandidate>
        {
            new(
                SubmissionDuplicates.ProbeId,
                DisplayName(probe.FirstName, probe.MiddleName, probe.LastName, probe.Suffix, probe.Nickname),
                probeBranch,
                null,
                null,
                probeEmail,
                true,
                null)
        };

        var approvedById = new Dictionary<int, Honoree>();
        foreach (var honoree in honorees)
        {
            if (honoree.Id == SubmissionDuplicates.ProbeId || honoree.Id == excludeHonoreeId)
            {
                continue;
            }

            approvedById[honoree.Id] = honoree;
            candidates.Add(new DuplicateCandidate(
                honoree.Id,
                DuplicateReviewGroups.DisplayName(honoree),
                honoree.ServiceBranch?.ServiceBranchName,
                honoree.FlagGrid?.FlagGridName,
                DuplicateReviewGroups.SponsorName(honoree.Sponsor),
                null,
                honoree.IsActive,
                honoree.DeletedDate));
        }

        var pendingById = new Dictionary<int, HonoreeChangeRequest>();
        foreach (var request in pending)
        {
            if (request.Id == excludeChangeRequestId || request.Id <= 0)
            {
                continue;
            }

            pendingById[request.Id] = request;
            candidates.Add(new DuplicateCandidate(
                -request.Id,
                DisplayName(request.FirstName, request.MiddleName, request.LastName, request.Suffix, request.Nickname),
                request.ServiceBranch?.ServiceBranchName,
                null,
                null,
                NormalizeEmail(string.IsNullOrWhiteSpace(request.SubmitterEmailAddress)
                    ? request.FlagClaim?.ExternalUserEmail
                    : request.SubmitterEmailAddress),
                true,
                null));
        }

        var best = new Dictionary<int, (string Likelihood, string Reason)>();
        foreach (var group in HonoreeDuplicateMatcher.Match(candidates))
        {
            foreach (var pair in group.Pairs)
            {
                if (pair.LeftId != SubmissionDuplicates.ProbeId && pair.RightId != SubmissionDuplicates.ProbeId)
                {
                    continue;
                }

                var other = pair.LeftId == SubmissionDuplicates.ProbeId ? pair.RightId : pair.LeftId;
                var likelihood = HonoreeDuplicateMatcher.LikelihoodOf(pair.ReasonCodes);
                if (likelihood == "Low")
                {
                    continue;
                }

                if (!best.TryGetValue(other, out var existing) || Rank(likelihood) < Rank(existing.Likelihood))
                {
                    best[other] = (likelihood, pair.Explanation);
                }
            }
        }

        var matches = new List<(int SortId, SubmissionMatchDto Match)>();
        foreach (var (id, hit) in best)
        {
            if (id > 0 && approvedById.TryGetValue(id, out var honoree))
            {
                var location = string.IsNullOrWhiteSpace(honoree.FlagGrid?.FlagGridName)
                    ? null
                    : honoree.FlagGrid!.FlagGridName;
                matches.Add((id, new SubmissionMatchDto(
                    "Approved",
                    honoree.Id,
                    null,
                    DuplicateReviewGroups.DisplayName(honoree),
                    BlankToNull(honoree.Rank),
                    BlankToNull(honoree.ServiceBranch?.ServiceBranchName),
                    location,
                    hit.Likelihood,
                    hit.Reason,
                    callerIsAdmin,
                    ProtectApproved(honoree.Id))));
            }
            else if (id < 0 && pendingById.TryGetValue(-id, out var request))
            {
                var ownerId = request.FlagClaim?.ExternalUserObjectId;
                var owns = !string.IsNullOrWhiteSpace(callerObjectId)
                    && string.Equals(ownerId, callerObjectId, StringComparison.Ordinal);
                var revealId = callerIsAdmin || owns;
                matches.Add((request.Id, new SubmissionMatchDto(
                    "Pending",
                    null,
                    revealId ? request.Id : null,
                    DisplayName(request.FirstName, request.MiddleName, request.LastName, request.Suffix, request.Nickname),
                    BlankToNull(request.Rank),
                    BlankToNull(request.ServiceBranch?.ServiceBranchName),
                    null,
                    hit.Likelihood,
                    hit.Reason,
                    owns && request.HonoreeId is not > 0 || callerIsAdmin,
                    ProtectPending(request.Id))));
            }
        }

        matches.Sort((left, right) =>
        {
            var rank = Rank(left.Match.Likelihood).CompareTo(Rank(right.Match.Likelihood));
            if (rank != 0)
            {
                return rank;
            }

            var name = string.Compare(left.Match.Name, right.Match.Name, StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : left.SortId.CompareTo(right.SortId);
        });
        return matches.Count <= 8
            ? matches.Select(item => item.Match).ToList()
            : matches.Take(8).Select(item => item.Match).ToList();
    }

    public async Task<FlagClaim?> FindByIdempotencyKeyAsync(Guid key, CancellationToken ct) =>
        await db.FlagClaims
            .Include(claim => claim.FlagGrid)
            .Include(claim => claim.Honoree)
            .Include(claim => claim.HonoreeChangeRequests)
            .FirstOrDefaultAsync(claim => claim.ClientRequestId == key, ct);

    public async Task<FlagClaim?> FindRecentIdenticalAsync(
        string? callerObjectId,
        string? firstName,
        string? middleName,
        string? lastName,
        string? suffix,
        string? nickname,
        string? rank,
        int? serviceBranchId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callerObjectId))
        {
            return null;
        }

        var cutoff = DateTime.UtcNow.Subtract(SubmissionDuplicates.DuplicatePostWindow);
        var recent = await db.FlagClaims
            .Include(claim => claim.FlagGrid)
            .Include(claim => claim.Honoree)
            .Include(claim => claim.HonoreeChangeRequests)
            .Where(claim => claim.ExternalUserObjectId == callerObjectId
                && claim.CreatedUtc >= cutoff
                && claim.HonoreeChangeRequests.Any(request => request.RequestStatus == "Submitted"))
            .ToListAsync(ct);

        return recent.FirstOrDefault(claim =>
        {
            var change = claim.HonoreeChangeRequests
                .OrderByDescending(request => request.Id)
                .FirstOrDefault(request => request.RequestStatus == "Submitted");
            return change is not null
                && SameNormalizedIdentity(firstName, middleName, lastName, suffix, nickname, rank, serviceBranchId, change);
        });
    }

    public static bool SameNormalizedIdentity(
        string? firstName,
        string? middleName,
        string? lastName,
        string? suffix,
        string? nickname,
        string? rank,
        int? serviceBranchId,
        HonoreeChangeRequest row) =>
        Fold(firstName) == Fold(row.FirstName)
        && Fold(middleName) == Fold(row.MiddleName)
        && Fold(lastName) == Fold(row.LastName)
        && Fold(suffix) == Fold(row.Suffix)
        && Fold(nickname) == Fold(row.Nickname)
        && Fold(rank) == Fold(row.Rank)
        && serviceBranchId == row.ServiceBranchId;

    public static string Fold(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    public async Task<SubmissionReceipt> EnsureReceiptAsync(int changeRequestId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await db.SubmissionReceipts
                .FirstOrDefaultAsync(receipt => receipt.ChangeRequestId == changeRequestId, ct);
            if (existing is not null)
            {
                if (string.IsNullOrWhiteSpace(existing.PublicCode))
                {
                    existing.PublicCode = await NextPublicCodeAsync(ct);
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException ex) when (UserProfileService.IsUniqueViolation(ex))
                    {
                        db.ChangeTracker.Clear();
                        continue;
                    }
                }

                return existing;
            }

            var receipt = new SubmissionReceipt
            {
                ChangeRequestId = changeRequestId,
                PublicToken = Guid.NewGuid(),
                PublicCode = await NextPublicCodeAsync(ct),
                CreatedUtc = DateTime.UtcNow
            };
            db.SubmissionReceipts.Add(receipt);
            try
            {
                await db.SaveChangesAsync(ct);
                return receipt;
            }
            catch (DbUpdateException ex) when (UserProfileService.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
            }
        }

        var saved = await db.SubmissionReceipts
            .FirstAsync(receipt => receipt.ChangeRequestId == changeRequestId, ct);
        return saved;
    }

    public static string CreatePublicCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return "PFOH-" + string.Create(6, alphabet, static (chars, symbols) =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
            }
        });
    }

    private async Task<string> NextPublicCodeAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var code = CreatePublicCode();
            var taken = await db.SubmissionReceipts.AnyAsync(row => row.PublicCode == code, ct);
            if (!taken)
            {
                return code;
            }
        }

        throw new InvalidOperationException("A submission reference could not be reserved.");
    }

    public async Task<SupersededSubmissionPage> ListSupersededAsync(int page, int pageSize, CancellationToken ct)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 50 ? 25 : pageSize;
        var audited = db.SubmissionAuditEvents
            .AsNoTracking()
            .Where(evt => evt.Action == SubmissionAuditEvent.Superseded);
        var query = db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(request => request.ServiceBranch)
            .Where(request => request.RequestStatus == PendingReviewMaintenance.Superseded
                && audited.Any(evt => evt.ChangeRequestId == request.Id));
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(request => request.SubmittedUtc ?? request.CreatedUtc)
            .ThenByDescending(request => request.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        var ids = rows.Select(request => request.Id).ToList();
        var events = await db.SubmissionAuditEvents
            .AsNoTracking()
            .Where(evt => ids.Contains(evt.ChangeRequestId) && evt.Action == SubmissionAuditEvent.Superseded)
            .ToListAsync(ct);
        var latest = events
            .GroupBy(evt => evt.ChangeRequestId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(evt => evt.Id).First());
        var keeperIds = latest.Values
            .Select(evt => evt.KeeperChangeRequestId)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var keepers = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(request => keeperIds.Contains(request.Id))
            .ToDictionaryAsync(request => request.Id, ct);

        var items = rows.Select(request =>
        {
            latest.TryGetValue(request.Id, out var audit);
            HonoreeChangeRequest? keeper = null;
            if (audit?.KeeperChangeRequestId is int keeperId)
            {
                keepers.TryGetValue(keeperId, out keeper);
            }

            var submitted = request.SubmittedUtc ?? request.CreatedUtc;
            return new SupersededSubmissionDto(
                request.Id,
                DisplayName(request.FirstName, request.MiddleName, request.LastName, request.Suffix, request.Nickname),
                BlankToNull(request.Rank),
                BlankToNull(request.ServiceBranch?.ServiceBranchName),
                submitted,
                CentralTime.Format(submitted),
                BlankToNull(request.SubmitterEmailAddress),
                audit?.KeeperChangeRequestId,
                keeper is null
                    ? null
                    : DisplayName(keeper.FirstName, keeper.MiddleName, keeper.LastName, keeper.Suffix, keeper.Nickname));
        }).ToList();
        return new SupersededSubmissionPage(items, page, pageSize, total);
    }

    public bool TryReadMatchToken(string? token, out string kind, out int id)
    {
        kind = string.Empty;
        id = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var raw = matchTokens.Unprotect(token);
            var split = raw.Split(':', 2);
            if (split.Length != 2 || !int.TryParse(split[1], out id) || id <= 0)
            {
                return false;
            }

            kind = split[0] switch
            {
                "p" => "Pending",
                "a" => "Approved",
                _ => string.Empty
            };
            return kind.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string ProtectPending(int id) => matchTokens.Protect($"p:{id}");

    private string ProtectApproved(int id) => matchTokens.Protect($"a:{id}");

    public async Task<bool> RestoreAsync(
        int changeRequestId,
        string actorEmail,
        string? actorObjectId,
        CancellationToken ct)
    {
        var request = await db.HonoreeChangeRequests
            .FirstOrDefaultAsync(row => row.Id == changeRequestId, ct);
        if (request is null || request.RequestStatus != PendingReviewMaintenance.Superseded)
        {
            return false;
        }

        request.RequestStatus = "Submitted";
        request.ReviewedUtc = null;
        var note = "Restored to the approval queue by an administrator.";
        request.ReviewNotes = string.IsNullOrWhiteSpace(request.ReviewNotes)
            ? note
            : $"{request.ReviewNotes.Trim()}\n{note}";
        db.SubmissionAuditEvents.Add(new SubmissionAuditEvent
        {
            Action = SubmissionAuditEvent.Restored,
            ChangeRequestId = request.Id,
            KeeperChangeRequestId = null,
            ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? "administrator" : actorEmail.Trim(),
            ActorObjectId = actorObjectId,
            CreatedUtc = DateTime.UtcNow,
            Note = note
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<SubmissionReceiptDto?> ReadAsync(Guid token, CancellationToken ct)
    {
        var receipt = await db.SubmissionReceipts
            .AsNoTracking()
            .Include(row => row.ChangeRequest)
            .ThenInclude(change => change!.ServiceBranch)
            .Include(row => row.ChangeRequest)
            .ThenInclude(change => change!.Honoree)
            .ThenInclude(honoree => honoree!.FlagGrid)
            .FirstOrDefaultAsync(row => row.PublicToken == token, ct);
        var change = receipt?.ChangeRequest;
        if (receipt is null || change is null)
        {
            return null;
        }

        string? location = null;
        if (change.RequestStatus == "Approved" && !string.IsNullOrWhiteSpace(change.Honoree?.FlagGrid?.FlagGridName))
        {
            location = change.Honoree!.FlagGrid!.FlagGridName;
        }

        var status = StatusLabel(change.RequestStatus, change.RequiresCardReprint);
        return new SubmissionReceiptDto(
            receipt.PublicCode,
            status,
            DisplayName(change.FirstName, change.MiddleName, change.LastName, change.Suffix, change.Nickname),
            BlankToNull(change.Rank),
            BlankToNull(change.ServiceBranch?.ServiceBranchName),
            location,
            BlankToNull(change.Description),
            NextStepsFor(status));
    }

    public static string StatusLabel(string? requestStatus, bool savedByAdmin = false)
    {
        if (savedByAdmin && string.Equals(requestStatus, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            return "Saved";
        }

        return requestStatus switch
        {
            "Approved" => "Approved",
            "Rejected" => "Needs revision",
            "Superseded" => "Superseded",
            "Draft" => "Draft",
            _ => "Pending review"
        };
    }

    public static string NextStepsFor(string status) => status switch
    {
        "Saved" => "The record was updated. An administrator can print a new card from the print queue.",
        "Approved" => "This submission has been approved.",
        "Needs revision" => "An administrator asked for a revision. Sign in and open My submissions to update it.",
        "Superseded" => "A newer submission from the same account replaced this one on the same day. An administrator can restore it.",
        _ => "An administrator reviews this submission. It appears on the field only after that approval."
    };

    public static string DisplayName(
        string? first,
        string? middle,
        string? last,
        string? suffix,
        string? nickname)
    {
        var name = string.Join(
            " ",
            new[] { first, middle, last, suffix }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return name;
        }

        return name.EndsWith($"({nickname})", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} ({nickname})";
    }

    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return email.Trim().ToLowerInvariant();
    }

    public static bool EmailsEqual(string? left, string? right)
    {
        var a = NormalizeEmail(left);
        var b = NormalizeEmail(right);
        return !string.IsNullOrEmpty(a) && a == b;
    }

    private static int Rank(string likelihood) => likelihood switch
    {
        "High" => 0,
        "Medium" => 1,
        _ => 2
    };

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class CentralTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public static DateOnly DateOf(DateTime utc) =>
        DateOnly.FromDateTime(Local(utc));

    public static string Format(DateTime utc)
    {
        var local = Local(utc);
        var label = Zone.IsDaylightSavingTime(local) ? "CDT" : "CST";
        return $"{local:MMM d, yyyy h:mm tt} {label}";
    }

    private static DateTime Local(DateTime utc)
    {
        var specified = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(specified, Zone);
    }
}
