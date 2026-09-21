using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed record UserProfileUpdateResult(bool Forbidden, string? Error, UserProfileDto? Profile);

public class UserProfileService(PfohDbContext db, ILogger<UserProfileService> logger)
{
    public async Task<UserProfileDto> GetOwnAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        await EnsureStoreAsync(ct);
        var ownerObjectId = user.GetExternalObjectId();
        var saved = await db.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(profile => profile.OwnerObjectId == ownerObjectId, ct);

        return ToDto(user, saved);
    }

    public async Task<UserProfileUpdateResult> UpdateOwnAsync(
        ClaimsPrincipal user,
        string routeOwnerObjectId,
        string? bodyOwnerObjectId,
        string? displayName,
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

        var email = user.GetEmail().Trim();
        if (email.Length > UserProfileLimits.EmailMaxLength)
        {
            email = email[..UserProfileLimits.EmailMaxLength];
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
                CreatedUtc = now,
                UpdatedUtc = now
            };
            db.UserProfiles.Add(profile);
        }
        else
        {
            profile.DisplayName = trimmed;
            profile.Email = email;
            profile.UpdatedUtc = now;
        }

        await ApplyDisplayNameToClaimsAsync(callerId, trimmed, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Retrying profile save for {OwnerObjectId} after a write conflict.", callerId);
            db.ChangeTracker.Clear();
            profile = await db.UserProfiles.FirstOrDefaultAsync(item => item.OwnerObjectId == callerId, ct);
            if (profile is null)
            {
                throw;
            }

            profile.DisplayName = trimmed;
            profile.Email = email;
            profile.UpdatedUtc = now;
            await ApplyDisplayNameToClaimsAsync(callerId, trimmed, ct);
            await db.SaveChangesAsync(ct);
        }

        return new UserProfileUpdateResult(false, null, ToDto(user, profile));
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

    private async Task ApplyDisplayNameToClaimsAsync(string ownerObjectId, string displayName, CancellationToken ct)
    {
        var claims = await db.FlagClaims
            .Where(claim => claim.ExternalUserObjectId == ownerObjectId)
            .ToListAsync(ct);

        foreach (var claim in claims)
        {
            claim.ExternalUserName = displayName;
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

    private static UserProfileDto ToDto(ClaimsPrincipal user, UserProfile? saved)
    {
        var tokenName = user.GetDisplayName();
        var displayName = saved is not null && !string.IsNullOrWhiteSpace(saved.DisplayName)
            ? saved.DisplayName.Trim()
            : IsPlaceholderDisplayName(tokenName) ? string.Empty : tokenName!.Trim();

        var email = user.GetEmail().Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            email = saved?.Email.Trim() ?? string.Empty;
        }

        return new UserProfileDto(
            user.GetExternalObjectId(),
            displayName,
            email,
            saved is not null && !string.IsNullOrWhiteSpace(saved.DisplayName));
    }
}
