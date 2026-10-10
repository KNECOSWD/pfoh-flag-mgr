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
    /// Phone from the access token. Prefers extension_{extensionsAppId}_Phone.
    /// extensionsAppId is the b2c-extensions-app client id. Null accepts any
    /// extension_{32 hex}_Phone claim, then standard phone claims. Missing claims return null.
    /// The value is not checked as E.164 here.
    /// </summary>
    public static string? GetPhoneNumber(this ClaimsPrincipal user, string? extensionsAppId = null)
    {
        return EntraExtensionClaims.GetPhone(user, extensionsAppId);
    }

    /// <summary>
    /// Organization from extension_{extensionsAppId}_Organization. Missing claims return null.
    /// </summary>
    public static string? GetOrganization(this ClaimsPrincipal user, string? extensionsAppId = null)
    {
        return EntraExtensionClaims.GetOrganization(user, extensionsAppId);
    }
}
