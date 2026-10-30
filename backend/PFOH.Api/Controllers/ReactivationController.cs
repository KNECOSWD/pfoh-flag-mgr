using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

/// <summary>
/// Public reactivation link. The token is the only secret in the URL.
/// Invalid links share one message. Nothing here sends email.
/// </summary>
[ApiController]
[Route("api/reactivate")]
[AllowAnonymous]
[EnableRateLimiting("Reactivate")]
public class ReactivationController(ReactivationLinkService links) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<ActionResult<ReactivationLinkStateDto>> Inspect(string token, CancellationToken ct)
    {
        var email = User.Identity?.IsAuthenticated == true ? User.GetEmail() : null;
        return Ok(await links.InspectAsync(token, email, ct));
    }

    [HttpPost("{token}")]
    public async Task<ActionResult<ReactivationLinkStateDto>> Submit(
        string token,
        [FromBody] ReactivationSubmitBody? body,
        CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Ok(await links.InspectAsync(token, signedInEmail: null, ct));
        }

        string objectId;
        try
        {
            objectId = User.GetExternalObjectId();
        }
        catch (InvalidOperationException)
        {
            return Ok(new ReactivationLinkStateDto(
                ReactivationLinkService.SignInStatus,
                ReactivationLinkService.SignInMessage,
                null));
        }

        var (state, error) = await links.SubmitAsync(
            token,
            objectId,
            User.GetEmail(),
            User.GetDisplayName(),
            body?.Note,
            ct);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        return Ok(state);
    }
}
