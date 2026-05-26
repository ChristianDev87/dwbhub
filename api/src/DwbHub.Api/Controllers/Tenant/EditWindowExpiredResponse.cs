namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Returned with HTTP 422 from PATCH /api/t/.../messages/{messageId} when the per-tenant
/// edit window has elapsed since the message was sent.
/// </summary>
/// <param name="Error">Stable machine-readable error code: <c>"edit_window_expired"</c>.</param>
/// <param name="AgeSeconds">Actual age of the message in seconds (always &gt; <paramref name="WindowSeconds"/>).</param>
/// <param name="WindowSeconds">Configured per-tenant edit window in seconds.</param>
public sealed record EditWindowExpiredResponse(string Error, int AgeSeconds, int WindowSeconds);
