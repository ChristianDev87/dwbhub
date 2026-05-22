using DwbHub.Application.Tenancy;
using DwbHub.Core.Entities;

namespace DwbHub.Infrastructure.Tenancy;

/// <summary>
/// Concrete impl with a public setter. Only TenantResolverMiddleware should
/// cast ITenantContext to TenantContext and call Current = ...; consumers
/// (services, controllers) only read via the interface.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Tenant? Current { get; set; }
    public bool IsResolved => Current is not null;
}
