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
}
