using DwbHub.Application.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Tenant-scoped dashboard endpoint. Returns summary metadata for the resolved tenant.
/// Requires a valid JWT with a matching <c>tid</c> claim.
/// </summary>
[ApiController]
[Authorize]
public sealed class TenantDashboardController(ITenantContext tenantContext) : ControllerBase
{
    /// <summary>Return tenant metadata (id, slug, name, locale) for the authenticated user's tenant.</summary>
    [HttpGet("/api/t/{slug}/dashboard")]
    [ProducesResponseType<DashboardResponse>(StatusCodes.Status200OK)]
    public IActionResult Get(string slug)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException(
                "TenantContext not populated despite matched /api/t/ route.");

        return Ok(new DashboardResponse(
            TenantId: tenant.Id,
            TenantSlug: tenant.Slug,
            TenantName: tenant.Name,
            Locale: tenant.Locale));
    }
}
