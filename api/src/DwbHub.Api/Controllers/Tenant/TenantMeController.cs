using System.Security.Claims;
using DwbHub.Api.Controllers.Auth;
using DwbHub.Application.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Returns caller identity scoped to a tenant. <see cref="ITenantContext.Current"/> is populated by TenantResolverMiddleware.
/// </summary>
[ApiController]
[Authorize]
public sealed class TenantMeController(ITenantContext tenantContext) : ControllerBase
{
    /// <summary>Return the caller's user ID, tenant ID, tenant slug, and role.</summary>
    [HttpGet("/api/t/{slug}/me")]
    public IActionResult Me(string slug)
    {
        _ = slug; // path param consumed by the middleware regex; we read from context

        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException(
                "TenantContext not populated despite matched /api/t/ route.");

        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var role = User.FindFirstValue("role") ?? "";

        if (sub is null)
        {
            return Unauthorized(new { error = "invalid_token" });
        }

        return Ok(new MeResponse(
            UserId: long.Parse(sub),
            TenantId: tenant.Id,
            TenantSlug: tenant.Slug,
            Role: role));
    }
}
