using System.Security.Claims;
using System.Text.RegularExpressions;

namespace PFOH.Api.Extensions;

/// <summary>
/// Reads Phone and Organization from an External ID token.
/// Claims mapping emits the short names phone and organization. Those win.
/// If they are absent, the directory extension names are used:
/// extension_{extensionsAppIdWithoutDashes}_Phone and _Organization.
/// The default extensions app id is the live b2c-extensions-app. Configuration can replace it.
/// A missing claim returns null. Phone is not checked as a US number here.
/// </summary>
public static partial class EntraExtensionClaims
{
    public const string DefaultExtensionsAppId = "a29a0dc7-4c9b-4cef-9f78-3444e62907d3";
    public const string PhoneAttribute = "Phone";
    public const string OrganizationAttribute = "Organization";

    [GeneratedRegex(@"^extension_([0-9a-fA-F]{32})_(Phone|Organization)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionClaimPattern();

    public static string? GetPhone(ClaimsPrincipal user, string? extensionsAppId)
    {
        return ShortClaim(user, "phone")
            ?? Find(user, extensionsAppId, PhoneAttribute)
            ?? StandardPhone(user);
    }

    public static string? GetOrganization(ClaimsPrincipal user, string? extensionsAppId)
    {
        return ShortClaim(user, "organization")
            ?? Find(user, extensionsAppId, OrganizationAttribute);
    }

    public static string? NormalizeAppId(string? extensionsAppId)
    {
        if (string.IsNullOrWhiteSpace(extensionsAppId))
        {
            return null;
        }

        var compact = extensionsAppId.Trim().Trim('{', '}').Replace("-", "", StringComparison.Ordinal);
        if (compact.Length != 32 || compact.Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }

        return compact.ToLowerInvariant();
    }

    private static string? ShortClaim(ClaimsPrincipal user, string name)
    {
        foreach (var claim in user.Claims)
        {
            if (!ClaimName(claim.Type).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(claim.Value))
            {
                continue;
            }

            return claim.Value.Trim();
        }

        return null;
    }

    private static string? Find(ClaimsPrincipal user, string? extensionsAppId, string attribute)
    {
        var configured = !string.IsNullOrWhiteSpace(extensionsAppId);
        var normalized = NormalizeAppId(extensionsAppId);
        if (configured && normalized is null)
        {
            return null;
        }

        foreach (var claim in user.Claims)
        {
            if (string.IsNullOrWhiteSpace(claim.Value))
            {
                continue;
            }

            var match = ExtensionClaimPattern().Match(ClaimName(claim.Type));
            if (!match.Success || !match.Groups[2].Value.Equals(attribute, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalized is not null && !match.Groups[1].Value.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return claim.Value.Trim();
        }

        return null;
    }

    private static string? StandardPhone(ClaimsPrincipal user)
    {
        foreach (var claim in user.Claims)
        {
            if (!IsStandardPhoneClaim(claim.Type) || string.IsNullOrWhiteSpace(claim.Value))
            {
                continue;
            }

            return claim.Value.Trim();
        }

        return null;
    }

    private static bool IsStandardPhoneClaim(string type)
    {
        var name = ClaimName(type);
        return name.Equals("phone_number", StringComparison.OrdinalIgnoreCase)
            || name.Equals("phoneNumber", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mobile_phone", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mobilePhone", StringComparison.OrdinalIgnoreCase)
            || name.Equals("telephoneNumber", StringComparison.OrdinalIgnoreCase)
            || name.Equals("signInNames.phoneNumber", StringComparison.OrdinalIgnoreCase)
            || name.Equals("strongAuthenticationPhoneNumber", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClaimName(string type)
    {
        var slash = type.LastIndexOf('/');
        return slash >= 0 ? type[(slash + 1)..] : type;
    }
}
