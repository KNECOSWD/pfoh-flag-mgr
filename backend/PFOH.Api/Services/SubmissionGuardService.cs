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
    ISubmissionMailer mailer,
    ILogger<SubmissionGuardService> logger)
{
    public bool Enabled => SubmissionDuplicates.Enabled(configuration);

    public async Task<IReadOnlyList<SubmissionMatchDto>> FindAsync(
        SubmissionProbe probe,
        int? excludeHonoreeId,
        int? excludeChangeRequestId,
        bool callerIsAdmin,
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

        var matches = new List<SubmissionMatchDto>();
        foreach (var (id, hit) in best)
        {
            if (id > 0 && approvedById.TryGetValue(id, out var honoree))
            {
                var location = string.IsNullOrWhiteSpace(honoree.FlagGrid?.FlagGridName)
                    ? null
                    : honoree.FlagGrid!.FlagGridName;
                matches.Add(new SubmissionMatchDto(
                    "Approved",
                    honoree.Id,
                    null,
                    DuplicateReviewGroups.DisplayName(honoree),
                    BlankToNull(honoree.Rank),
                    BlankToNull(honoree.ServiceBranch?.ServiceBranchName),
                    location,
                    hit.Likelihood,
                    hit.Reason,
                    callerIsAdmin));
            }
            else if (id < 0 && pendingById.TryGetValue(-id, out var request))
            {
                var pendingEmail = NormalizeEmail(string.IsNullOrWhiteSpace(request.SubmitterEmailAddress)
                    ? request.FlagClaim?.ExternalUserEmail
                    : request.SubmitterEmailAddress);
                matches.Add(new SubmissionMatchDto(
                    "Pending",
                    null,
                    request.Id,
                    DisplayName(request.FirstName, request.MiddleName, request.LastName, request.Suffix, request.Nickname),
                    BlankToNull(request.Rank),
                    BlankToNull(request.ServiceBranch?.ServiceBranchName),
                    null,
                    hit.Likelihood,
                    hit.Reason,
                    callerIsAdmin || EmailsEqual(probeEmail, pendingEmail)));
            }
        }

        matches.Sort((left, right) =>
        {
            var rank = Rank(left.Likelihood).CompareTo(Rank(right.Likelihood));
            return rank != 0 ? rank : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        return matches.Count <= 8 ? matches : matches.Take(8).ToList();
    }

    public async Task<FlagClaim?> FindByIdempotencyKeyAsync(Guid key, CancellationToken ct) =>
        await db.FlagClaims
            .Include(claim => claim.FlagGrid)
            .Include(claim => claim.Honoree)
            .Include(claim => claim.HonoreeChangeRequests)
            .FirstOrDefaultAsync(claim => claim.ClientRequestId == key, ct);

    public async Task<FlagClaim?> FindRecentIdenticalAsync(
        string? email,
        string firstName,
        string lastName,
        int? serviceBranchId,
        CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var cutoff = DateTime.UtcNow.Subtract(SubmissionDuplicates.DuplicatePostWindow);
        var recent = await db.FlagClaims
            .Include(claim => claim.FlagGrid)
            .Include(claim => claim.Honoree)
            .Include(claim => claim.HonoreeChangeRequests)
            .Where(claim => claim.CreatedUtc >= cutoff && claim.HonoreeChangeRequests.Any(request => request.RequestStatus == "Submitted"))
            .ToListAsync(ct);

        return recent.FirstOrDefault(claim =>
        {
            var change = claim.HonoreeChangeRequests
                .OrderByDescending(request => request.Id)
                .FirstOrDefault(request => request.RequestStatus == "Submitted");
            if (change is null)
            {
                return false;
            }

            return EmailsEqual(normalized, NormalizeEmail(change.SubmitterEmailAddress) ?? NormalizeEmail(claim.ExternalUserEmail))
                && string.Equals(change.FirstName.Trim(), firstName.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(change.LastName.Trim(), lastName.Trim(), StringComparison.OrdinalIgnoreCase)
                && change.ServiceBranchId == serviceBranchId;
        });
    }

    public async Task SupersedeSameDayAsync(
        HonoreeChangeRequest keeper,
        string? submitterEmail,
        string? actorObjectId,
        CancellationToken ct)
    {
        var email = NormalizeEmail(submitterEmail);
        if (string.IsNullOrEmpty(email))
        {
            return;
        }

        var keeperInstant = keeper.SubmittedUtc ?? keeper.CreatedUtc;
        var keeperDay = CentralTime.DateOf(keeperInstant);
        var pending = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .Where(request => request.RequestStatus == "Submitted" && request.Id != keeper.Id)
            .ToListAsync(ct);

        var older = pending.Where(request =>
            request.HonoreeId is not > 0
            && request.CorrectsChangeRequestId is not > 0
            && EmailsEqual(email, NormalizeEmail(string.IsNullOrWhiteSpace(request.SubmitterEmailAddress)
                ? request.FlagClaim?.ExternalUserEmail
                : request.SubmitterEmailAddress))
            && CentralTime.DateOf(request.SubmittedUtc ?? request.CreatedUtc) == keeperDay
            && (request.SubmittedUtc ?? request.CreatedUtc) <= keeperInstant).ToList();

        if (older.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var request in older)
        {
            request.RequestStatus = PendingReviewMaintenance.Superseded;
            var note = $"Superseded by a newer submission from the same email on this day. Change request {keeper.Id} remains in review.";
            request.ReviewNotes = string.IsNullOrWhiteSpace(request.ReviewNotes)
                ? note
                : request.ReviewNotes.Contains(note, StringComparison.Ordinal)
                    ? request.ReviewNotes
                    : $"{request.ReviewNotes.Trim()}\n{note}";
            db.SubmissionAuditEvents.Add(new SubmissionAuditEvent
            {
                Action = SubmissionAuditEvent.Superseded,
                ChangeRequestId = request.Id,
                KeeperChangeRequestId = keeper.Id,
                ActorEmail = email,
                ActorObjectId = actorObjectId,
                CreatedUtc = now,
                Note = note
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<SubmissionReceipt> EnsureReceiptAsync(int changeRequestId, CancellationToken ct)
    {
        var existing = await db.SubmissionReceipts
            .FirstOrDefaultAsync(receipt => receipt.ChangeRequestId == changeRequestId, ct);
        if (existing is not null)
        {
            return existing;
        }

        var receipt = new SubmissionReceipt
        {
            ChangeRequestId = changeRequestId,
            PublicToken = Guid.NewGuid(),
            CreatedUtc = DateTime.UtcNow
        };
        db.SubmissionReceipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        return receipt;
    }

    public async Task SendConfirmationAsync(
        HonoreeChangeRequest change,
        SubmissionReceipt receipt,
        string status,
        string nextSteps,
        HttpRequest request,
        CancellationToken ct)
    {
        var email = NormalizeEmail(change.SubmitterEmailAddress);
        if (string.IsNullOrEmpty(email))
        {
            return;
        }

        var name = DisplayName(change.FirstName, change.MiddleName, change.LastName, change.Suffix, change.Nickname);
        var reference = receipt.PublicToken.ToString("D");
        var url = $"{ReactivationLinkService.PublicBaseUrl(configuration, request)}/submission/{reference}";
        var body = $"""
            We received your submission for {name}.

            Status: {status}
            Reference: {reference}

            {nextSteps}

            You can view this submission at {url}

            Plano Flags of Honor
            """;
        try
        {
            await mailer.SendConfirmationAsync(
                new SubmissionConfirmation(
                    email,
                    "We received your Plano Flags of Honor submission",
                    body,
                    reference),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Submission confirmation was not recorded for reference {Reference}.", reference);
        }
    }

    public async Task<IReadOnlyList<SupersededSubmissionDto>> ListSupersededAsync(CancellationToken ct)
    {
        var rows = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(request => request.ServiceBranch)
            .Where(request => request.RequestStatus == PendingReviewMaintenance.Superseded)
            .OrderByDescending(request => request.SubmittedUtc ?? request.CreatedUtc)
            .Take(50)
            .ToListAsync(ct);

        return rows.Select(request => new SupersededSubmissionDto(
            request.Id,
            DisplayName(request.FirstName, request.MiddleName, request.LastName, request.Suffix, request.Nickname),
            BlankToNull(request.Rank),
            BlankToNull(request.ServiceBranch?.ServiceBranchName),
            request.SubmittedUtc ?? request.CreatedUtc)).ToList();
    }

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
            receipt.PublicToken.ToString("D"),
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
        "Superseded" => "A newer submission from the same email replaced this one on the same day. An administrator can restore it.",
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

    public static DateOnly DateOf(DateTime utc)
    {
        var specified = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(specified, Zone));
    }
}
