using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/profile")]
public class ProfileController(UserProfileService profiles) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<UserProfileDto>> Get(CancellationToken ct)
    {
        return Ok(await profiles.GetOwnAsync(User, ct));
    }

    [HttpPut]
    public Task<ActionResult<UserProfileDto>> UpdateOwn(
        [FromBody] UpdateUserProfileRequest request,
        CancellationToken ct)
    {
        return Update(User.GetExternalObjectId(), request, ct);
    }

    [HttpPut("{ownerObjectId}")]
    public async Task<ActionResult<UserProfileDto>> Update(
        string ownerObjectId,
        [FromBody] UpdateUserProfileRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { message = "Display name is required." });
        }

        var result = await profiles.UpdateOwnAsync(
            User,
            ownerObjectId,
            request.OwnerObjectId,
            request.DisplayName,
            ct);

        if (result.Forbidden)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "You can only update your own profile." });
        }

        if (result.Error is not null)
        {
            return BadRequest(new { message = result.Error });
        }

        return Ok(result.Profile);
    }
}
