using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using PFOH.Api.Extensions;

namespace PFOH.Api.Services;

/// <summary>
/// Upserts the signed-in user's profile once per token claim set.
/// The cache key is a hash. The claim values are not written to logs.
/// </summary>
public sealed class UserProfileProvisioningMiddleware(RequestDelegate next, IMemoryCache cache)
{
    public async Task InvokeAsync(HttpContext context, UserProfileService profiles, ILogger<UserProfileProvisioningMiddleware> logger)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var key = CacheKey(context.User);
            if (key is not null && !cache.TryGetValue(key, out _))
            {
                try
                {
                    await profiles.EnsureProvisionedAsync(context.User, context.RequestAborted);
                    cache.Set(key, 1, TimeSpan.FromHours(12));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        "User profile provisioning failed ({ExceptionType}).",
                        ex.GetType().Name);
                }
            }
        }

        await next(context);
    }

    internal static string? CacheKey(ClaimsPrincipal user)
    {
        string ownerObjectId;
        try
        {
            ownerObjectId = user.GetExternalObjectId();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var material = string.Join(
            '\n',
            ownerObjectId,
            user.GetEmail(),
            user.GetDisplayName() ?? string.Empty,
            user.GetPhoneNumber() ?? string.Empty,
            user.GetOrganization() ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "pfoh-profile:" + Convert.ToHexString(hash);
    }
}
