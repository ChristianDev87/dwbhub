namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Returned with HTTP 429 from POST /api/t/{slug}/guilds/{publicId}/bot/reconnect
/// when the per-guild manual-reconnect cool-down (60s) is still active. The
/// caller should wait <see cref="RetryAfterSeconds"/> before retrying; the same
/// value is also surfaced in the Retry-After response header.
/// </summary>
/// <param name="Error">Stable machine-readable error code: <c>"manual_reconnect_cooldown"</c>.</param>
/// <param name="RetryAfterSeconds">Remaining cool-down window in seconds (always ≥1).</param>
public sealed record ManualReconnectCoolDownResponse(string Error, int RetryAfterSeconds);
