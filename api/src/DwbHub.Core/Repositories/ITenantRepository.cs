using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Tenant-table operations. Unlike every other repository in the system,
/// methods here do NOT take a `long tenantId` argument — tenants IS the tenant.
/// </summary>
public interface ITenantRepository
{
    /// <summary>Load a tenant by its internal primary key. Returns null when the id does not exist.</summary>
    Task<Tenant?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Load a tenant by its URL slug (case-insensitive via CITEXT). Returns null when the slug does not exist.</summary>
    Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Insert a new tenant row. Returns the generated internal id.
    /// </summary>
    Task<long> CreateAsync(string name, string slug, string locale = "de", CancellationToken ct = default);

    /// <summary>List every tenant in the system, ordered by name. Returns an empty list when none exist.</summary>
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Overwrite the per-tenant message edit-window setting.
    /// Pass <c>null</c> to reset to the system default.
    /// Returns <c>true</c> when the row was updated, <c>false</c> when the id was not found.
    /// </summary>
    Task<bool> UpdateMessageEditWindowAsync(long id, int? windowSeconds, CancellationToken ct = default);
}
