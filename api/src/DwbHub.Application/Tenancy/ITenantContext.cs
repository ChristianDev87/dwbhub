using DwbHub.Core.Entities;

namespace DwbHub.Application.Tenancy;

/// <summary>
/// Scoped resolver populated by TenantResolverMiddleware when a request URL
/// matches `/api/t/{slug}/...`. For non-tenant-scoped routes, Current stays null.
/// </summary>
public interface ITenantContext
{
    Tenant? Current { get; }
    bool IsResolved => Current is not null;
}
