using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PFOH.Api.Dtos;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/submissions")]
public class SubmissionReceiptsController(SubmissionGuardService submissions) : ControllerBase
{
    [HttpGet("{token:guid}")]
    [EnableRateLimiting("SubmissionReceipt")]
    public async Task<ActionResult<SubmissionReceiptDto>> Get(Guid token, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        var receipt = await submissions.ReadAsync(token, ct);
        if (receipt is null)
        {
            return NotFound(new { message = "This submission link is not valid." });
        }

        return Ok(receipt);
    }
}
