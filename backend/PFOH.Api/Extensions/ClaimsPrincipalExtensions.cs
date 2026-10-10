using System.Security.Claims;

namespace PFOH.Api.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static string GetExternalObjectId(this ClaimsPrincipal user)
    {
        var value =
            user.FindFirstValue("oid") ??
            user.FindFirstValue("sub") ??
            user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier") ??
            user.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Authenticated user is missing an object identifier claim.");
        }

        return value;
    }

    public static string GetEmail(this ClaimsPrincipal user)
    {
        return
            user.FindFirstValue("emails") ??
            user.FindFirstValue("email") ??
            user.FindFirstValue("preferred_username") ??
            user.FindFirstValue(ClaimTypes.Email) ??
            string.Empty;
    }

    public static string? GetDisplayName(this ClaimsPrincipal user)
    {
        return
            user.FindFirstValue("name") ??
            user.FindFirstValue("given_name") ??
            user.Identity?.Name;
    }

    /// <summary>
    /// Phone from the access token. The short claim phone wins, then
    /// extension_{extensionsAppId}_Phone, then a standard phone claim such as phone_number.
    /// A blank extensions app id accepts any extension_{32 hex}_Phone claim.
    /// Missing claims return null. The value is not checked as a US number here.
    /// </summary>
    public static string? GetPhoneNumber(this ClaimsPrincipal user, string? extensionsAppId = null)
    {
        return EntraExtensionClaims.GetPhone(user, extensionsAppId);
    }

    /// <summary>
    /// Organization from the short claim organization, then extension_{extensionsAppId}_Organization.
    /// Missing claims return null.
    /// </summary>
    public static string? GetOrganization(this ClaimsPrincipal user, string? extensionsAppId = null)
    {
        return EntraExtensionClaims.GetOrganization(user, extensionsAppId);
    }
}
