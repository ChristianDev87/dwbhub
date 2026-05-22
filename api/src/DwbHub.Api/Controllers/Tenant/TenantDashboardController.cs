using DwbHub.Application.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

[ApiController]
[Authorize]
public sealed class TenantDashboardController(ITenantContext tenantContext) : ControllerBase
{
    [HttpGet("/api/t/{slug}/dashboard")]
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
