using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private static readonly DateTimeOffset s_startedAt = DateTimeOffset.UtcNow;
    private static readonly string s_version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    [HttpGet]
    public IActionResult Get()
    {
        var payload = new
        {
            status = "ok",
            version = s_version,
            uptime_seconds = (long)(DateTimeOffset.UtcNow - s_startedAt).TotalSeconds,
        };
        return Ok(payload);
    }
}
