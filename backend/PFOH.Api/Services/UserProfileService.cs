using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed record UserProfileUpdateResult(bool Forbidden, string? Error, UserProfileDto? Profile);

public sealed record UserAccountContact(string? Phone, string? Organization);

public class UserProfileService(PfohDbContext db, ILogger<UserProfileService> logger, IConfiguration configuration)
{
    /// <summary>
    /// Fixed stripes. A map keyed by object id would grow for every account that ever signed in.
    /// </summary>
    private const int ProvisionGateStripes = 32;
    private static readonly SemaphoreSlim[] ProvisionGates = CreateProvisionGates();

    private static SemaphoreSlim[] CreateProvisionGates()
    {
        var gates = new SemaphoreSlim[ProvisionGateStripes];
        for (var i = 0; i < gates.Length; i++)
        {
            gates[i] = new SemaphoreSlim(1, 1);
        }

        return gates;
    }

    internal static int ProvisionGateCount => ProvisionGates.Length;

    internal static SemaphoreSlim ProvisionGateFor(string ownerObjectId)
    {
        var hash = (uint)StringComparer.Ordinal.GetHashCode(ownerObjectId);
        return ProvisionGates[hash % (uint)ProvisionGates.Length];
    }
    public async Task<UserProfileDto> GetOwnAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        await EnsureProvisionedAsync(user, ct);
        var ownerObjectId = user.GetExternalObjectId();
        var saved = await db.UserProfiles
            .FirstOrDefaultAsync(profile => profile.OwnerObjectId == ownerObjectId, ct);

        var rawClaimPhone = user.GetPhoneNumber(ExtensionsAppId);
        var phone = ResolvePhone(user, saved, ExtensionsAppId);
        var organization = ResolveOrganization(user, saved, ExtensionsAppId);
        await TryPersistClaimFillAsync(saved, phone, organization, ct);

        return ToDto(user, saved, phone, organization, PhoneNeedsCorrection(saved, rawClaimPhone, phone));
    }

    public async Task<UserProfileUpdateResult> UpdateOwnAsync(
        ClaimsPrincipal user,
        string routeOwnerObjectId,
        string? bodyOwnerObjectId,
        string? displayName,
        string? phone,
        string? organization,
        CancellationToken ct)
    {
        var callerId = user.GetExternalObjectId();
        if (!OwnsProfile(callerId, routeOwnerObjectId) || !OwnsProfile(callerId, bodyOwnerObjectId))
        {
            return new UserProfileUpdateResult(true, null, null);
        }

        var validationError = ValidateDisplayName(displayName, out var trimmed);
        if (validationError is not null)
        {
            return new UserProfileUpdateResult(false, validationError, null);
        }

        await EnsureStoreAsync(ct);

        var existing = await db.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.OwnerObjectId == callerId, ct);

        var email = ResolveEmail(user, existing);
        if (string.IsNullOrWhiteSpace(email))
        {
            return new UserProfileUpdateResult(false, "Email is required.", null);
        }

        if (!UsPhone.TryNormalize(phone, out var normalizedPhone))
        {
            return new UserProfileUpdateResult(false, UsPhone.InvalidMessage, null);
        }

        var organizationError = ValidateOrganization(organization, out var normalizedOrganization);
        if (organizationError is not null)
        {
            return new UserProfileUpdateResult(false, organizationError, null);
        }

        var now = DateTime.UtcNow;
        var profile = await db.UserProfiles
            .FirstOrDefaultAsync(item => item.OwnerObjectId == callerId, ct);

        if (profile is null)
        {
            profile = new UserProfile
            {
                OwnerObjectId = callerId,
                DisplayName = trimmed,
                Email = email,
                Phone = normalizedPhone,
                Organization = normalizedOrganization,
                DisplayNameSource = ProfileFieldSource.Profile,
                PhoneSource = ProfileFieldSource.Profile,
                OrganizationSource = string.IsNullOrWhiteSpace(normalizedOrganization)
                    ? ProfileFieldSource.Claim
                    : ProfileFieldSource.Profile,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            db.UserProfiles.Add(profile);
        }
        else
        {
            profile.DisplayName = trimmed;
            profile.Email = email;
            profile.Phone = normalizedPhone;
            profile.Organization = normalizedOrganization;
            profile.DisplayNameSource = ProfileFieldSource.Profile;
            profile.PhoneSource = ProfileFieldSource.Profile;
            profile.OrganizationSource = string.IsNullOrWhiteSpace(normalizedOrganization)
                ? ProfileFieldSource.Claim
                : ProfileFieldSource.Profile;
            profile.UpdatedUtc = now;
        }

        await ApplyDisplayNameToClaimsAsync(callerId, trimmed, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            LogWriteFailure(ex, "Retrying profile save after a write conflict.");
            db.ChangeTracker.Clear();
            profile = await db.UserProfiles.FirstOrDefaultAsync(item => item.OwnerObjectId == callerId, ct);
            if (profile is null)
            {
                throw;
            }

            profile.DisplayName = trimmed;
            profile.Email = email;
            profile.Phone = normalizedPhone;
            profile.Organization = normalizedOrganization;
            profile.DisplayNameSource = ProfileFieldSource.Profile;
            profile.PhoneSource = ProfileFieldSource.Profile;
            profile.OrganizationSource = string.IsNullOrWhiteSpace(normalizedOrganization)
                ? ProfileFieldSource.Claim
                : ProfileFieldSource.Profile;
            profile.UpdatedUtc = now;
            await ApplyDisplayNameToClaimsAsync(callerId, trimmed, ct);
            await db.SaveChangesAsync(ct);
        }

        return new UserProfileUpdateResult(false, null, ToDto(user, profile, normalizedPhone, normalizedOrganization ?? string.Empty));
    }

    public async Task<IReadOnlyDictionary<string, UserAccountContact>> GetAccountContactsAsync(
        IEnumerable<string> ownerObjectIds,
        CancellationToken ct)
    {
        var ids = ownerObjectIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<string, UserAccountContact>(StringComparer.OrdinalIgnoreCase);
        }

        await EnsureStoreAsync(ct);

        var profiles = await db.UserProfiles
            .AsNoTracking()
            .Where(profile => ids.Contains(profile.OwnerObjectId))
            .Select(profile => new { profile.OwnerObjectId, profile.Phone, profile.Organization })
            .ToListAsync(ct);

        return profiles
            .GroupBy(profile => profile.OwnerObjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var profile = group.First();
                    return new UserAccountContact(BlankToNull(profile.Phone), BlankToNull(profile.Organization));
                },
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Prefer the saved profile name so new flag claims do not keep a placeholder token name.
    /// Falls back to the token name if the profile store cannot be read.
    /// </summary>
    public async Task<string?> ResolveDisplayNameAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            await EnsureStoreAsync(ct);
            var ownerObjectId = user.GetExternalObjectId();
            var saved = await db.UserProfiles
                .AsNoTracking()
                .Where(profile => profile.OwnerObjectId == ownerObjectId)
                .Select(profile => profile.DisplayName)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(saved))
            {
                return saved.Trim();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saved profile display name could not be read. Using the token name.");
        }

        return user.GetDisplayName();
    }

    public static bool IsPlaceholderDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("unknown unknown", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ValidateDisplayName(string? displayName, out string trimmed)
    {
        trimmed = displayName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "Display name is required.";
        }

        if (trimmed.Length > UserProfileLimits.DisplayNameMaxLength)
        {
            return $"Display name must be {UserProfileLimits.DisplayNameMaxLength} characters or fewer.";
        }

        if (trimmed.Any(char.IsControl))
        {
            return "Display name cannot include line breaks or control characters.";
        }

        return null;
    }

    public static string? ValidateOrganization(string? organization, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim();
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Length > UserProfileLimits.OrganizationMaxLength)
        {
            return $"Organization must be {UserProfileLimits.OrganizationMaxLength} characters or fewer.";
        }

        if (normalized.Any(char.IsControl))
        {
            return "Organization cannot include line breaks or control characters.";
        }

        return null;
    }

    private async Task TryPersistClaimFillAsync(
        UserProfile? saved,
        string phone,
        string organization,
        CancellationToken ct)
    {
        if (saved is null)
        {
            return;
        }

        var dirty = false;
        if (!string.IsNullOrWhiteSpace(phone) && !UsPhone.TryNormalize(saved.Phone, out _))
        {
            saved.Phone = phone;
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(saved.Organization) && !string.IsNullOrWhiteSpace(organization))
        {
            saved.Organization = organization;
            dirty = true;
        }

        if (!dirty)
        {
            return;
        }

        saved.UpdatedUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            LogWriteFailure(ex, "Phone or organization from the sign-in token could not be saved.");
        }
    }

    private static string ResolvePhone(ClaimsPrincipal user, UserProfile? saved, string? extensionsAppId)
    {
        if (saved is not null && UsPhone.TryNormalize(saved.Phone, out var savedPhone))
        {
            return savedPhone;
        }

        return UsPhone.TryNormalize(user.GetPhoneNumber(extensionsAppId), out var claimPhone) ? claimPhone : string.Empty;
    }

    private static string ResolveOrganization(ClaimsPrincipal user, UserProfile? saved, string? extensionsAppId)
    {
        if (saved is not null && !string.IsNullOrWhiteSpace(saved.Organization))
        {
            return saved.Organization.Trim();
        }

        var claim = user.GetOrganization(extensionsAppId);
        if (string.IsNullOrWhiteSpace(claim))
        {
            return string.Empty;
        }

        var trimmed = claim.Trim();
        return trimmed.Length <= UserProfileLimits.OrganizationMaxLength
            ? trimmed
            : trimmed[..UserProfileLimits.OrganizationMaxLength];
    }

    private static string ResolveEmail(ClaimsPrincipal user, UserProfile? saved)
    {
        var email = user.GetEmail().Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            email = saved?.Email.Trim() ?? string.Empty;
        }

        if (email.Length > UserProfileLimits.EmailMaxLength)
        {
            email = email[..UserProfileLimits.EmailMaxLength];
        }

        return email;
    }

    private async Task ApplyDisplayNameToClaimsAsync(string ownerObjectId, string displayName, CancellationToken ct)
    {
        var claims = await db.FlagClaims
            .Where(claim => claim.ExternalUserObjectId == ownerObjectId)
            .ToListAsync(ct);

        foreach (var claim in claims)
        {
            claim.ExternalUserName = displayName;
        }

        var ownership = await db.HonoreeClaims
            .Where(claim => claim.UserId == ownerObjectId)
            .ToListAsync(ct);
        foreach (var claim in ownership)
        {
            claim.DisplayName = displayName;
        }
    }

    /// <summary>
    /// Inserts the caller's profile from the token, or refreshes claim-sourced fields
    /// on the row keyed by the Entra object id. Email is not an owner key.
    /// A unique violation from a concurrent insert is re-read and then refreshed.
    /// </summary>
    public async Task EnsureProvisionedAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        await EnsureStoreAsync(ct);
        var ownerObjectId = user.GetExternalObjectId();
        var gate = ProvisionGateFor(ownerObjectId);
        await gate.WaitAsync(ct);
        try
        {
            var incoming = ReadClaimFields(user);
            var existing = await OwnerProfiles(ownerObjectId).FirstOrDefaultAsync(ct);

            if (existing is null)
            {
                var created = NewClaimProfile(ownerObjectId, incoming);
                db.UserProfiles.Add(created);
                try
                {
                    await db.SaveChangesAsync(ct);
                    return;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    logger.LogInformation("Profile insert raced. Reading the existing row.");
                    db.ChangeTracker.Clear();
                    existing = await OwnerProfiles(ownerObjectId).FirstOrDefaultAsync(ct);
                    if (existing is null)
                    {
                        throw;
                    }
                }
            }

            if (!ApplyClaimRefresh(existing, incoming))
            {
                return;
            }

            existing.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Oldest row first, then lowest id, so a duplicated object id always updates the same row.
    /// </summary>
    private IQueryable<UserProfile> OwnerProfiles(string ownerObjectId)
    {
        return db.UserProfiles
            .Where(profile => profile.OwnerObjectId == ownerObjectId)
            .OrderBy(profile => profile.CreatedUtc)
            .ThenBy(profile => profile.Id);
    }

    /// <summary>
    /// Index creation can fail with a SQL error whose message contains an object id.
    /// The log keeps the error number and leaves the exception, and its message, out.
    /// </summary>
    internal static void LogStartupIndexFailure(ILogger logger, Exception ex)
    {
        logger.LogError(
            "STARTUP CLEANUP FAILED while ensuring dbo.UserProfiles. The site will keep running. SqlErrorNumber={SqlErrorNumber}",
            SqlErrorNumber(ex));
    }

    /// <summary>
    /// Logs the exception type and the SQL Server error number only.
    /// The duplicate-key message contains the object id, so the exception object and its message stay out of the log.
    /// </summary>
    private void LogWriteFailure(Exception ex, string message)
    {
        logger.LogWarning(
            "{Failure} ExceptionType={ExceptionType} SqlErrorNumber={SqlErrorNumber}",
            message,
            ex.GetType().FullName,
            SqlErrorNumber(ex));
    }

    internal static int? SqlErrorNumber(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is SqlException sql)
            {
                return sql.Number;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return null;
    }

    /// <summary>
    /// SQL Server unique-index violations are 2601 and 2627.
    /// Tests throw <c>Microsoft.Data.Sqlite.SqliteException</c>; that constraint code is 19
    /// (extended unique-constraint code 2067). The API does not reference SQLite, so the
    /// code is read from the exception. Message text is not used.
    /// </summary>
    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        var pending = new Stack<Exception>();
        pending.Push(ex);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is SqlException sql && sql.Number is 2601 or 2627)
            {
                return true;
            }

            if (IsSqliteConstraint(current))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return false;
    }

    private static bool IsSqliteConstraint(Exception exception)
    {
        const int sqliteConstraint = 19;
        const int sqliteConstraintUnique = 2067;
        if (!string.Equals(exception.GetType().FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal))
        {
            return false;
        }

        var errorCode = ReadSqliteCode(exception, "SqliteErrorCode");
        var extended = ReadSqliteCode(exception, "SqliteExtendedErrorCode");
        return errorCode == sqliteConstraint
            || extended == sqliteConstraint
            || extended == sqliteConstraintUnique
            || (errorCode & 0xFF) == sqliteConstraint;
    }

    private static int ReadSqliteCode(Exception exception, string propertyName)
    {
        var value = exception.GetType().GetProperty(propertyName)?.GetValue(exception);
        return value is int code ? code : 0;
    }

    private static bool HasProfileDisplayName(UserProfile? saved)
    {
        if (saved is null || string.IsNullOrWhiteSpace(saved.DisplayName))
        {
            return false;
        }

        return !string.Equals(saved.DisplayNameSource, ProfileFieldSource.Claim, StringComparison.Ordinal);
    }

    private ClaimFields ReadClaimFields(ClaimsPrincipal user)
    {
        var name = user.GetDisplayName();
        string? displayName = null;
        if (!IsPlaceholderDisplayName(name))
        {
            displayName = name!.Trim();
            if (displayName.Length > UserProfileLimits.DisplayNameMaxLength)
            {
                displayName = displayName[..UserProfileLimits.DisplayNameMaxLength];
            }
        }

        var email = user.GetEmail().Trim();
        if (email.Length > UserProfileLimits.EmailMaxLength)
        {
            email = email[..UserProfileLimits.EmailMaxLength];
        }

        string? phone = UsPhone.TryNormalize(user.GetPhoneNumber(ExtensionsAppId), out var normalizedPhone)
            ? normalizedPhone
            : null;

        var organizationClaim = user.GetOrganization(ExtensionsAppId);
        string? organization = null;
        if (!string.IsNullOrWhiteSpace(organizationClaim))
        {
            var trimmed = organizationClaim.Trim();
            organization = trimmed.Length <= UserProfileLimits.OrganizationMaxLength
                ? trimmed
                : trimmed[..UserProfileLimits.OrganizationMaxLength];
        }

        return new ClaimFields(displayName, email, phone, organization);
    }

    private static UserProfile NewClaimProfile(string ownerObjectId, ClaimFields incoming)
    {
        var now = DateTime.UtcNow;
        return new UserProfile
        {
            OwnerObjectId = ownerObjectId,
            DisplayName = incoming.DisplayName ?? string.Empty,
            Email = incoming.Email,
            Phone = incoming.Phone ?? string.Empty,
            Organization = incoming.Organization,
            DisplayNameSource = ProfileFieldSource.Claim,
            PhoneSource = ProfileFieldSource.Claim,
            OrganizationSource = ProfileFieldSource.Claim,
            CreatedUtc = now,
            UpdatedUtc = now
        };
    }

    /// <summary>
    /// Copies usable claim values onto claim-sourced fields. An empty claim does not
    /// clear a value already stored. Profile-sourced fields, and null sources on
    /// older rows, are not overwritten. A blank organization is still filled once,
    /// matching the previous sign-in behavior.
    /// </summary>
    private static bool ApplyClaimRefresh(UserProfile profile, ClaimFields incoming)
    {
        var dirty = false;
        if (!string.IsNullOrWhiteSpace(incoming.Email)
            && !string.Equals(profile.Email, incoming.Email, StringComparison.Ordinal))
        {
            profile.Email = incoming.Email;
            dirty = true;
        }

        if (IsClaimSource(profile.DisplayNameSource) && incoming.DisplayName is not null
            && !string.Equals(profile.DisplayName, incoming.DisplayName, StringComparison.Ordinal))
        {
            profile.DisplayName = incoming.DisplayName;
            profile.DisplayNameSource = ProfileFieldSource.Claim;
            dirty = true;
        }

        if (IsClaimSource(profile.PhoneSource) && incoming.Phone is not null
            && !string.Equals(profile.Phone, incoming.Phone, StringComparison.Ordinal))
        {
            profile.Phone = incoming.Phone;
            profile.PhoneSource = ProfileFieldSource.Claim;
            dirty = true;
        }
        else if (!IsClaimSource(profile.PhoneSource)
            && !UsPhone.TryNormalize(profile.Phone, out _)
            && incoming.Phone is not null)
        {
            profile.Phone = incoming.Phone;
            dirty = true;
        }

        if (IsClaimSource(profile.OrganizationSource)
            && incoming.Organization is not null
            && !string.Equals(profile.Organization, incoming.Organization, StringComparison.Ordinal))
        {
            profile.Organization = incoming.Organization;
            profile.OrganizationSource = ProfileFieldSource.Claim;
            dirty = true;
        }
        else if (string.IsNullOrWhiteSpace(profile.Organization) && incoming.Organization is not null)
        {
            profile.Organization = incoming.Organization;
            if (IsClaimSource(profile.OrganizationSource) || string.IsNullOrEmpty(profile.OrganizationSource))
            {
                profile.OrganizationSource = ProfileFieldSource.Claim;
            }

            dirty = true;
        }

        return dirty;
    }

    private static bool IsClaimSource(string? source)
    {
        return string.Equals(source, ProfileFieldSource.Claim, StringComparison.Ordinal);
    }

    private readonly record struct ClaimFields(string? DisplayName, string Email, string? Phone, string? Organization);

    private string ExtensionsAppId
    {
        get
        {
            var configured = configuration["Entra:ExtensionsAppId"];
            return string.IsNullOrWhiteSpace(configured)
                ? EntraExtensionClaims.DefaultExtensionsAppId
                : configured.Trim();
        }
    }

    private async Task EnsureStoreAsync(CancellationToken ct)
    {
        try
        {
            await UserProfileSchema.EnsureAsync(db, logger, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to prepare the user profile table.");
            throw new InvalidOperationException("The profile store is unavailable.", ex);
        }
    }

    private static bool OwnsProfile(string callerId, string? requestedOwnerObjectId)
    {
        if (string.IsNullOrWhiteSpace(requestedOwnerObjectId))
        {
            return true;
        }

        return string.Equals(callerId, requestedOwnerObjectId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? BlankToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool PhoneNeedsCorrection(UserProfile? saved, string? rawClaimPhone, string resolvedPhone)
    {
        if (UsPhone.TryNormalize(resolvedPhone, out _))
        {
            return false;
        }

        var savedIsUnusable = saved is not null && !string.IsNullOrWhiteSpace(saved.Phone);
        var claimIsUnusable = !string.IsNullOrWhiteSpace(rawClaimPhone);
        return savedIsUnusable || claimIsUnusable;
    }

    private static UserProfileDto ToDto(
        ClaimsPrincipal user,
        UserProfile? saved,
        string phone,
        string organization,
        bool phoneNeedsCorrection = false)
    {
        var tokenName = user.GetDisplayName();
        var hasSavedDisplayName = HasProfileDisplayName(saved);
        var displayName = hasSavedDisplayName
            ? saved!.DisplayName.Trim()
            : IsPlaceholderDisplayName(tokenName) ? string.Empty : tokenName!.Trim();

        var email = ResolveEmail(user, saved);

        return new UserProfileDto(
            user.GetExternalObjectId(),
            displayName,
            email,
            hasSavedDisplayName,
            phone,
            organization,
            phoneNeedsCorrection);
    }
}
