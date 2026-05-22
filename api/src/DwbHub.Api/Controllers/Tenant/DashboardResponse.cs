namespace DwbHub.Api.Controllers.Tenant;

public sealed record DashboardResponse(
    long TenantId,
    string TenantSlug,
    string TenantName,
    string Locale);
