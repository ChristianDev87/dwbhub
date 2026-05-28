using System.Security.Claims;
using DwbHub.Application.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Tenant-scoped settings endpoints. Requires the Owner role for all mutations.
/// The TenantResolverMiddleware has already validated tenant slug + JWT.tid before
/// this controller runs.
/// </summary>
[ApiController]
[Route("/api/t/{slug}/settings")]
public sealed class TenantSettingsController(
    ITenantContext tenantContext,
    ITenantSettingsService settingsService) : ControllerBase
{
    // Range defined by the DB CHECK constraint in migration 018.
    private const int MinWindowSeconds = 60;
    private const int MaxWindowSeconds = 31536000;

    /// <summary>
    /// Update tenant-level settings. Requires the Owner role.
    /// Returns 204 No Content on success.
    /// Returns 400 Bad Request with <c>{ "error": "out_of_range" }</c> when
    /// <see cref="PatchTenantSettingsRequest.MessageEditWindowSeconds"/> is outside [60, 31536000].
    /// </summary>
    [HttpPatch]
    [Authorize(Roles = "Owner")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Patch(string slug, [FromBody] PatchTenantSettingsRequest body, CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");

        if (body.MessageEditWindowSeconds is int w &&
            (w < MinWindowSeconds || w > MaxWindowSeconds))
        {
            return BadRequest(new { error = "out_of_range" });
        }

        var actorUserId = ExtractUserId()
            ?? throw new InvalidOperationException("missing sub claim");

        await settingsService.UpdateMessageEditWindowAsync(
            tenant.Id,
            body.MessageEditWindowSeconds,
            actorUserId,
            ct).ConfigureAwait(false);

        return NoContent();
    }

    private long? ExtractUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out var v) ? v : null;
    }
}
