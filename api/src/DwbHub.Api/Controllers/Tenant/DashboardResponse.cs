namespace DwbHub.Api.Controllers.Tenant;

/// <summary>Response body for GET /api/t/{slug}/dashboard.</summary>
/// <param name="TenantId">Internal ID of the tenant.</param>
/// <param name="TenantSlug">URL slug of the tenant.</param>
/// <param name="TenantName">Human-readable name of the tenant.</param>
/// <param name="Locale">BCP-47 locale tag configured for the tenant.</param>
public sealed record DashboardResponse(
    long TenantId,
    string TenantSlug,
    string TenantName,
    string Locale);
