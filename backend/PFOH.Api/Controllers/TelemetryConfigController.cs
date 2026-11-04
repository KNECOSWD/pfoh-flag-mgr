using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/telemetry-config")]
[AllowAnonymous]
public class TelemetryConfigController(IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        Response.Headers.CacheControl = "no-store";
        var connectionString = BrowserTelemetryConfig.ReadConnectionString(configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Ok(new { enabled = false });
        }

        return Ok(new { enabled = true, connectionString });
    }
}
