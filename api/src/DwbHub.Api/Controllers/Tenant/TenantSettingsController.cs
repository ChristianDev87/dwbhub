using System.Security.Claims;
using DwbHub.Application.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

[ApiController]
[Route("api/t/{slug}/settings")]
[Authorize(Roles = "Owner")]
public sealed class TenantSettingsController(
    ITenantSettingsService settings,
    ITenantContext tenantContext) : ControllerBase
{
    /// <summary>
    /// Update per-tenant settings. Currently supports only the message-edit-window.
    /// Owner-only.
    /// </summary>
    [HttpPatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Patch(
        string slug,
        [FromBody] PatchTenantSettingsRequest body,
        CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");
        var actorUserId = long.Parse(
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new InvalidOperationException("Authorized action without user id claim."));

        try
        {
            await settings.UpdateMessageEditWindowAsync(tenant.Id, body.MessageEditWindowSeconds, actorUserId, ct)
                .ConfigureAwait(false);
            return NoContent();
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(new { error = "out_of_range" });
        }
    }
}
