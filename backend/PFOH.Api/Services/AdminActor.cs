using System.Security.Claims;
using PFOH.Api.Extensions;

namespace PFOH.Api.Services;

public sealed record AdminActor(string Email, string? ObjectId)
{
    public static AdminActor From(ClaimsPrincipal user)
    {
        var email = user.GetEmail();
        if (string.IsNullOrWhiteSpace(email))
        {
            email = user.GetDisplayName() ?? "PFOH.Admin";
        }

        email = email.Trim();
        if (email.Length > 256)
        {
            email = email[..256];
        }

        string? objectId = null;
        try
        {
            objectId = user.GetExternalObjectId();
        }
        catch (InvalidOperationException)
        {
            objectId = null;
        }

        if (objectId is { Length: > 128 })
        {
            objectId = objectId[..128];
        }

        return new AdminActor(email, objectId);
    }
}
