using System.Text.Json.Serialization;

namespace DwbHub.Api.Controllers;

/// <summary>Response body for GET /api/health.</summary>
/// <param name="Status">Always <c>"ok"</c> when the service is healthy.</param>
/// <param name="Version">The informational assembly version.</param>
/// <param name="UptimeSeconds">Seconds elapsed since the process started.</param>
public sealed record HealthResponse(
    string Status,
    string Version,
    [property: JsonPropertyName("uptime_seconds")] long UptimeSeconds);
