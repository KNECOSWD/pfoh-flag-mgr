using System.Security.Claims;
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
    public async Task<UserProfileDto> GetOwnAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        await EnsureStoreAsync(ct);
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
            profile.UpdatedUtc = now;
        }

        await ApplyDisplayNameToClaimsAsync(callerId, trimmed, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            logger.LogWarning("Profile save hit a write conflict and will be retried.");
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
        catch (Exception)
        {
            logger.LogWarning("Phone or organization from the sign-in token could not be saved.");
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
            await UserProfileSchema.EnsureAsync(db, ct);
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
        var displayName = saved is not null && !string.IsNullOrWhiteSpace(saved.DisplayName)
            ? saved.DisplayName.Trim()
            : IsPlaceholderDisplayName(tokenName) ? string.Empty : tokenName!.Trim();

        var email = ResolveEmail(user, saved);

        return new UserProfileDto(
            user.GetExternalObjectId(),
            displayName,
            email,
            saved is not null && !string.IsNullOrWhiteSpace(saved.DisplayName),
            phone,
            organization,
            phoneNeedsCorrection);
    }
}
