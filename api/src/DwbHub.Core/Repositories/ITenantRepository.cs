using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Tenant-table operations. Unlike every other repository in the system,
/// methods here do NOT take a `long tenantId` argument — tenants IS the tenant.
/// </summary>
public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<long> CreateAsync(string name, string slug, CancellationToken ct = default);
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default);
}
