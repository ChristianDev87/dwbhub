using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Per-tenant user lookup. All methods take <c>tenantId</c> first as a scoping guard.
/// </summary>
public interface IUserRepository
{
    /// <summary>Load a user by email within a tenant. Returns null when no matching row exists.</summary>
    Task<User?> GetByEmailAsync(long tenantId, string email, CancellationToken ct = default);

    /// <summary>Load a user by internal id within a tenant. Returns null when the id does not exist or belongs to a different tenant.</summary>
    Task<User?> GetByIdAsync(long tenantId, long id, CancellationToken ct = default);

    /// <summary>Insert a new user row. Returns the generated internal id.</summary>
    Task<long> CreateAsync(User user, CancellationToken ct = default);
}
