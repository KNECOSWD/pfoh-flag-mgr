using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/version")]
[AllowAnonymous]
public class VersionController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var assembly = typeof(VersionController).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        var version = (plus >= 0 ? informational[..plus] : informational).Trim();
        var commit = plus >= 0 ? informational[(plus + 1)..].Trim() : "";
        var builtAt = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildTime")
            ?.Value ?? "";

        return Ok(new
        {
            version,
            commit,
            builtAt
        });
    }
}
