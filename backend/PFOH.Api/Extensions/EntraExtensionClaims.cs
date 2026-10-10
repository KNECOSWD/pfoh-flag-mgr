using System.Security.Claims;
using System.Text.RegularExpressions;

namespace PFOH.Api.Extensions;

/// <summary>
/// Reads the External ID custom attributes Phone and Organization.
/// Entra stores them on b2c-extensions-app and emits them only after a claims-mapping
/// policy. The JWT claim names are extension_{extensionsAppIdWithoutDashes}_Phone and
/// extension_{extensionsAppIdWithoutDashes}_Organization.
/// The extensions app id comes from configuration (Entra:ExtensionsAppId). It is not
/// hard-coded. A missing claim, or a missing id, returns null.
/// </summary>
public static partial class EntraExtensionClaims
{
    public const string PhoneAttribute = "Phone";
    public const string OrganizationAttribute = "Organization";

    [GeneratedRegex(@"^extension_([0-9a-fA-F]{32})_(Phone|Organization)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionClaimPattern();

    public static string? GetPhone(ClaimsPrincipal user, string? extensionsAppId)
    {
        return Find(user, extensionsAppId, PhoneAttribute) ?? StandardPhone(user);
    }

    public static string? GetOrganization(ClaimsPrincipal user, string? extensionsAppId)
    {
        return Find(user, extensionsAppId, OrganizationAttribute);
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
            || name.Equals("phone", StringComparison.OrdinalIgnoreCase)
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
