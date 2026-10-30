using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed class ReactivationLinkService(
    PfohDbContext db,
    IConfiguration configuration,
    ILogger<ReactivationLinkService> logger,
    PendingReviewMaintenance pendingReviews)
{
    public const string Eligible = "Eligible";
    public const string NotFound = "not found";
    public const string AlreadyActive = "already active";
    public const string HasFlag = "has a flag";
    public const string NoEmail = "no email";
    public const string EmailMismatch = "email mismatch";

    public const string InvalidStatus = "invalid";
    public const string SignInStatus = "sign-in";
    public const string MismatchStatus = "email-mismatch";
    public const string ReadyStatus = "ready";
    public const string SubmittedStatus = "submitted";

    public const string InvalidMessage = "This reactivation link cannot be used.";
    public const string SignInMessage = "Sign in with the email address this link was sent to, or create an account with that email.";
    public const string MismatchMessage = "This link was sent to a different email address. Sign in with that address to request reactivation.";
    public const string ReadyMessage = "Review the honoree below, then request reactivation. An administrator reviews the request before the honoree can return to the field.";
    public const string SubmittedMessage = "Your reactivation request was submitted. An administrator will review it before the honoree can return to the field.";

    public const int NoteMaxLength = 2000;

    public static int LifetimeDays(IConfiguration configuration)
    {
        var days = configuration.GetValue("Reactivation:LifetimeDays", 60);
        return days < 1 ? 60 : days;
    }

    /// <summary>
    /// Link format: {base}/reactivate/{token}. base is App:PublicBaseUrl when set,
    /// otherwise the scheme and host of the current request, with no trailing slash.
    /// The token is the base64url form of 32 random bytes, with no padding.
    /// </summary>
    public static string PublicBaseUrl(IConfiguration configuration, HttpRequest request)
    {
        var configured = configuration["App:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().TrimEnd('/');
        }

        return $"{request.Scheme}://{request.Host.Value}".TrimEnd('/');
    }

    public static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    public async Task<ReactivationLinkStateDto> InspectAsync(string? token, string? signedInEmail, CancellationToken ct)
    {
        var loaded = await LoadUsableTokenAsync(token, ct);
        if (loaded is null)
        {
            return Invalid();
        }

        if (NormalizeEmail(signedInEmail) is null)
        {
            return new ReactivationLinkStateDto(SignInStatus, SignInMessage, null);
        }

        if (!EmailsMatch(signedInEmail, loaded.Value.Token.RequesterEmail))
        {
            return new ReactivationLinkStateDto(MismatchStatus, MismatchMessage, null);
        }

        var summary = await SummaryAsync(loaded.Value.Honoree, ct);
        return new ReactivationLinkStateDto(ReadyStatus, ReadyMessage, summary);
    }

    public async Task<(ReactivationLinkStateDto State, string? Error)> SubmitAsync(
        string? token,
        string objectId,
        string email,
        string? displayName,
        string? note,
        CancellationToken ct)
    {
        if (note is not null && note.Trim().Length > NoteMaxLength)
        {
            return (Invalid(), "Note must be 2000 characters or fewer.");
        }

        var loaded = await LoadUsableTokenAsync(token, ct);
        if (loaded is null)
        {
            return (Invalid(), null);
        }

        if (!EmailsMatch(email, loaded.Value.Token.RequesterEmail))
        {
            return (new ReactivationLinkStateDto(MismatchStatus, MismatchMessage, null), null);
        }

        var honoree = loaded.Value.Honoree;
        var stored = loaded.Value.Token;
        var now = DateTime.UtcNow;
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var claim = new FlagClaim
        {
            FlagGridId = null,
            HonoreeId = honoree.Id,
            ExternalUserObjectId = objectId,
            ExternalUserEmail = NormalizeEmail(email) ?? email.Trim(),
            ExternalUserName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            ClaimStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        };

        var change = new HonoreeChangeRequest
        {
            FlagGridId = null,
            HonoreeId = honoree.Id,
            FirstName = honoree.FirstName,
            MiddleName = honoree.MiddleName,
            LastName = honoree.LastName,
            Suffix = honoree.Suffix,
            Nickname = honoree.Nickname,
            Rank = honoree.Rank,
            ServiceBranchId = honoree.ServiceBranchId,
            ServiceBranchCategoryId = honoree.ServiceBranchCategoryId,
            StartYear = honoree.StartYear,
            EndYear = honoree.EndYear,
            DatesUserEntry = honoree.DatesUserEntry,
            ConflictsServed = honoree.ConflictsServed,
            Awards = honoree.Awards,
            Description = honoree.Description,
            KIA = honoree.KIA,
            PhotoFileName = honoree.PhotoFileName,
            SubmitterPhoneNumber = honoree.PhoneNumber,
            SubmitterEmailAddress = stored.RequesterEmail,
            RequestStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        };

        claim.HonoreeChangeRequests.Add(change);
        db.FlagClaims.Add(claim);

        var tracked = await db.HonoreeReactivationTokens.FirstAsync(row => row.Id == stored.Id, ct);
        if (tracked.UsedUtc is not null || tracked.RevokedUtc is not null || tracked.ExpiresUtc <= now)
        {
            return (Invalid(), null);
        }

        tracked.UsedUtc = now;
        db.HonoreeReactivationRequests.Add(new HonoreeReactivationRequest
        {
            HonoreeId = honoree.Id,
            ChangeRequest = change,
            TokenId = tracked.Id,
            RequesterNote = trimmedNote,
            LinkUsedUtc = now,
            CreatedUtc = now
        });

        await db.SaveChangesAsync(ct);
        await pendingReviews.SupersedeOlderPendingAsync(change, ct);
        ReactivationAudit.Used(logger, honoree.Id);
        return (new ReactivationLinkStateDto(SubmittedStatus, SubmittedMessage, null), null);
    }

    public async Task<IReadOnlyList<ReactivationEligibleDto>> ListEligibleAsync(CancellationToken ct)
    {
        var context = await LoadContextAsync(null, ct);
        var active = await ActiveExpiryByHonoreeAsync(ct);
        return context.Honorees
            .Where(honoree => Reason(honoree, context, rawCsvEmail: null) is null)
            .OrderBy(honoree => honoree.LastName)
            .ThenBy(honoree => honoree.FirstName)
            .ThenBy(honoree => honoree.Id)
            .Select(honoree => new ReactivationEligibleDto(
                honoree.Id,
                DisplayName(honoree),
                context.EmailByHonoree[honoree.Id],
                active.TryGetValue(honoree.Id, out var expiry) ? expiry : null))
            .ToList();
    }

    public async Task<IReadOnlyList<ReactivationLinkRowDto>> GenerateAsync(
        IReadOnlyList<int>? honoreeIds,
        string createdBy,
        string baseUrl,
        CancellationToken ct)
    {
        var context = await LoadContextAsync(honoreeIds, ct);
        if (honoreeIds is null || honoreeIds.Count == 0)
        {
            var eligible = context.Honorees
                .Where(honoree => Reason(honoree, context, rawCsvEmail: null) is null)
                .OrderBy(honoree => honoree.LastName)
                .ThenBy(honoree => honoree.FirstName)
                .ThenBy(honoree => honoree.Id)
                .Select(honoree => (honoree.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)null))
                .ToList();
            return await MaterializeAsync(eligible, context, createdBy, baseUrl, ct);
        }

        var requested = honoreeIds
            .Select(id => (id.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)null))
            .ToList();
        return await MaterializeAsync(requested, context, createdBy, baseUrl, ct);
    }

    public async Task<IReadOnlyList<ReactivationLinkRowDto>> GenerateFromCsvAsync(
        string? csv,
        string createdBy,
        string baseUrl,
        CancellationToken ct)
    {
        var parsed = ReactivationCsv.Parse(csv);
        var ids = parsed
            .Select(row => int.TryParse(row.HonoreeId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var context = await LoadContextAsync(ids, ct);
        return await MaterializeAsync(parsed, context, createdBy, baseUrl, ct);
    }

    public async Task<bool> RevokeAsync(int honoreeId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var active = await db.HonoreeReactivationTokens
            .Where(token => token.HonoreeId == honoreeId && token.UsedUtc == null && token.RevokedUtc == null)
            .ToListAsync(ct);
        if (active.Count == 0)
        {
            return false;
        }

        foreach (var token in active)
        {
            token.RevokedUtc = now;
        }

        await db.SaveChangesAsync(ct);
        ReactivationAudit.Revoked(logger, honoreeId);
        return true;
    }

    private async Task<IReadOnlyList<ReactivationLinkRowDto>> MaterializeAsync(
        IReadOnlyList<(string HonoreeId, string? Email)> rows,
        CandidateContext context,
        string createdBy,
        string baseUrl,
        CancellationToken ct)
    {
        var issued = new Dictionary<int, ReactivationLinkRowDto>();
        var results = new List<ReactivationLinkRowDto>(rows.Count);
        foreach (var row in rows)
        {
            if (!int.TryParse(row.HonoreeId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) ||
                id <= 0)
            {
                results.Add(Blank(row.HonoreeId, NotFound));
                continue;
            }

            var honoree = context.Honorees.FirstOrDefault(item => item.Id == id);
            var reason = Reason(honoree, context, row.Email);
            if (reason is not null || honoree is null)
            {
                var email = reason == EmailMismatch
                    ? NormalizeEmail(row.Email) ?? row.Email?.Trim() ?? string.Empty
                    : honoree is not null && context.EmailByHonoree.TryGetValue(honoree.Id, out var stored)
                        ? stored
                        : string.Empty;
                results.Add(new ReactivationLinkRowDto(
                    row.HonoreeId,
                    honoree is null ? string.Empty : DisplayName(honoree),
                    reason == NoEmail ? string.Empty : email,
                    string.Empty,
                    string.Empty,
                    reason ?? NotFound));
                continue;
            }

            if (!issued.TryGetValue(honoree.Id, out var existing))
            {
                existing = await IssueAsync(honoree, context.EmailByHonoree[honoree.Id], createdBy, baseUrl, ct);
                issued[honoree.Id] = existing;
            }

            results.Add(existing);
        }

        return results;
    }

    private async Task<ReactivationLinkRowDto> IssueAsync(
        Honoree honoree,
        string email,
        string createdBy,
        string baseUrl,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var active = await db.HonoreeReactivationTokens
            .Where(token => token.HonoreeId == honoree.Id && token.UsedUtc == null && token.RevokedUtc == null)
            .ToListAsync(ct);
        foreach (var previous in active)
        {
            previous.RevokedUtc = now;
        }

        var secret = ReactivationTokenSecret.Create();
        if (!ReactivationTokenSecret.TryHash(secret, out var hash))
        {
            throw new InvalidOperationException("A reactivation token could not be hashed.");
        }

        var expires = now.AddDays(LifetimeDays(configuration));
        db.HonoreeReactivationTokens.Add(new HonoreeReactivationToken
        {
            HonoreeId = honoree.Id,
            TokenHash = hash,
            RequesterEmail = email,
            CreatedUtc = now,
            ExpiresUtc = expires,
            CreatedBy = TrimActor(createdBy)
        });
        await db.SaveChangesAsync(ct);
        ReactivationAudit.Generated(logger, honoree.Id);
        return new ReactivationLinkRowDto(
            honoree.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DisplayName(honoree),
            email,
            $"{baseUrl}/reactivate/{secret}",
            expires.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            Eligible);
    }

    private async Task<(HonoreeReactivationToken Token, Honoree Honoree)?> LoadUsableTokenAsync(string? token, CancellationToken ct)
    {
        var hashed = ReactivationTokenSecret.TryHash(token, out var hash);
        var stored = hashed
            ? await db.HonoreeReactivationTokens.AsNoTracking().FirstOrDefaultAsync(row => row.TokenHash == hash, ct)
            : null;
        var compareTo = stored?.TokenHash ?? ReactivationTokenSecret.DummyHash;
        var matches = hashed && stored is not null && ReactivationTokenSecret.HashEquals(compareTo, hash);
        if (!matches)
        {
            ReactivationTokenSecret.HashEquals(hash, ReactivationTokenSecret.DummyHash);
            return null;
        }

        var now = DateTime.UtcNow;
        if (stored!.UsedUtc is not null || stored.RevokedUtc is not null || stored.ExpiresUtc <= now)
        {
            return null;
        }

        var honoree = await db.Honorees.AsNoTracking().FirstOrDefaultAsync(item => item.Id == stored.HonoreeId, ct);
        if (honoree is null || honoree.IsActive || await HasFlagAsync(honoree, ct))
        {
            return null;
        }

        return (stored, honoree);
    }

    private async Task<bool> HasFlagAsync(Honoree honoree, CancellationToken ct)
    {
        if (honoree.FlagGridId is not null)
        {
            return true;
        }

        return await db.FlagGrids.AsNoTracking().AnyAsync(
            grid => grid.HonoreeId == honoree.Id && grid.DeletedDate == null,
            ct);
    }

    private async Task<ReactivationSummaryDto> SummaryAsync(Honoree honoree, CancellationToken ct)
    {
        string? branch = null;
        if (honoree.ServiceBranchId is int branchId)
        {
            branch = await db.ServiceBranches.AsNoTracking()
                .Where(item => item.Id == branchId)
                .Select(item => item.ServiceBranchName)
                .FirstOrDefaultAsync(ct);
        }

        return new ReactivationSummaryDto(
            DisplayName(honoree),
            BlankToNull(honoree.Rank),
            BlankToNull(branch),
            Years(honoree),
            BlankToNull(honoree.ConflictsServed),
            BlankToNull(honoree.Awards),
            BlankToNull(honoree.Description),
            honoree.KIA);
    }

    private async Task<Dictionary<int, DateTime>> ActiveExpiryByHonoreeAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await db.HonoreeReactivationTokens.AsNoTracking()
            .Where(token => token.UsedUtc == null && token.RevokedUtc == null && token.ExpiresUtc > now)
            .Select(token => new { token.HonoreeId, token.ExpiresUtc })
            .ToListAsync(ct);
        return rows
            .GroupBy(row => row.HonoreeId)
            .ToDictionary(group => group.Key, group => group.Max(row => row.ExpiresUtc));
    }

    private async Task<CandidateContext> LoadContextAsync(IReadOnlyCollection<int>? ids, CancellationToken ct)
    {
        var query = db.Honorees.AsNoTracking().Include(honoree => honoree.Sponsor).AsQueryable();
        if (ids is { Count: > 0 })
        {
            var idList = ids.Distinct().ToList();
            query = query.Where(honoree => idList.Contains(honoree.Id));
        }

        var honorees = await query.ToListAsync(ct);
        var honoreeIds = honorees.Select(honoree => honoree.Id).ToList();
        var flagged = honoreeIds.Count == 0
            ? new HashSet<int>()
            : (await db.FlagGrids.AsNoTracking()
                .Where(grid => grid.HonoreeId != null && honoreeIds.Contains(grid.HonoreeId.Value) && grid.DeletedDate == null)
                .Select(grid => grid.HonoreeId!.Value)
                .ToListAsync(ct)).ToHashSet();

        var claims = honoreeIds.Count == 0
            ? new List<ClaimEmail>()
            : await db.HonoreeClaims.AsNoTracking()
                .Where(claim => honoreeIds.Contains(claim.HonoreeId) && !claim.UserId.StartsWith("admin-reprint:"))
                .Select(claim => new ClaimEmail(claim.HonoreeId, claim.Email, claim.ClaimedUtc))
                .ToListAsync(ct);
        var claimEmail = claims
            .GroupBy(claim => claim.HonoreeId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(claim => claim.ClaimedUtc)
                    .Select(claim => NormalizeEmail(claim.Email))
                    .FirstOrDefault(email => email is not null));

        var emails = new Dictionary<int, string>();
        var flaggedIds = new HashSet<int>();
        foreach (var honoree in honorees)
        {
            if (honoree.FlagGridId is not null || flagged.Contains(honoree.Id))
            {
                flaggedIds.Add(honoree.Id);
            }

            var email = RequesterEmail(honoree, claimEmail.TryGetValue(honoree.Id, out var claim) ? claim : null);
            if (email is not null)
            {
                emails[honoree.Id] = email;
            }
        }

        return new CandidateContext(honorees, emails, flaggedIds);
    }

    internal static string? RequesterEmail(Honoree honoree, string? claimEmail)
    {
        var sponsor = NormalizeEmail(honoree.Sponsor?.EmailAddress);
        if (sponsor is not null)
        {
            return sponsor;
        }

        if (claimEmail is not null)
        {
            return claimEmail;
        }

        return NormalizeEmail(honoree.EmailAddress);
    }

    private static string? Reason(Honoree? honoree, CandidateContext context, string? rawCsvEmail)
    {
        if (honoree is null)
        {
            return NotFound;
        }

        if (honoree.IsActive)
        {
            return AlreadyActive;
        }

        if (context.FlaggedIds.Contains(honoree.Id))
        {
            return HasFlag;
        }

        if (!context.EmailByHonoree.TryGetValue(honoree.Id, out var stored))
        {
            return NoEmail;
        }

        if (rawCsvEmail is null)
        {
            return null;
        }

        var csvEmail = NormalizeEmail(rawCsvEmail);
        if (csvEmail is null || !string.Equals(csvEmail, stored, StringComparison.Ordinal))
        {
            return EmailMismatch;
        }

        return null;
    }

    private static bool EmailsMatch(string? signedIn, string bound) =>
        string.Equals(NormalizeEmail(signedIn), bound, StringComparison.Ordinal);

    private static ReactivationLinkStateDto Invalid() =>
        new(InvalidStatus, InvalidMessage, null);

    private static ReactivationLinkRowDto Blank(string honoreeId, string reason) =>
        new(honoreeId, string.Empty, string.Empty, string.Empty, string.Empty, reason);

    internal static string DisplayName(Honoree honoree)
    {
        var parts = new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim());
        var name = string.Join(' ', parts);
        if (!string.IsNullOrWhiteSpace(honoree.Suffix))
        {
            name = string.IsNullOrEmpty(name) ? honoree.Suffix.Trim() : $"{name} {honoree.Suffix.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(honoree.Nickname))
        {
            name = string.IsNullOrEmpty(name)
                ? $"({honoree.Nickname.Trim()})"
                : $"{name} ({honoree.Nickname.Trim()})";
        }

        return string.IsNullOrWhiteSpace(name) ? "Honoree" : name;
    }

    private static string? Years(Honoree honoree)
    {
        var entered = BlankToNull(honoree.DatesUserEntry);
        if (entered is not null)
        {
            return entered;
        }

        if (honoree.StartYear is null && honoree.EndYear is null)
        {
            return null;
        }

        if (honoree.StartYear is int start && honoree.EndYear is int end)
        {
            return $"{start}-{end}";
        }

        return (honoree.StartYear ?? honoree.EndYear)?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TrimActor(string createdBy)
    {
        var trimmed = string.IsNullOrWhiteSpace(createdBy) ? "PFOH.Admin" : createdBy.Trim();
        return trimmed.Length <= 255 ? trimmed : trimmed[..255];
    }

    private sealed record ClaimEmail(int HonoreeId, string Email, DateTime ClaimedUtc);

    private sealed record CandidateContext(
        List<Honoree> Honorees,
        Dictionary<int, string> EmailByHonoree,
        HashSet<int> FlaggedIds);
}
