using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers;

/// <summary>
/// Liveness probe endpoint at /api/health. Returns API version and uptime.
/// </summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private static readonly DateTimeOffset s_startedAt = DateTimeOffset.UtcNow;
    private static readonly string s_version =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    /// <summary>Return status, version, and uptime in seconds. No authentication required.</summary>
    [HttpGet]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        var payload = new HealthResponse(
            Status: "ok",
            Version: s_version,
            UptimeSeconds: (long)(DateTimeOffset.UtcNow - s_startedAt).TotalSeconds);
        return Ok(payload);
    }
}
