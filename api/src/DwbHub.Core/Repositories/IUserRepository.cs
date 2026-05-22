using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Per-tenant user lookup. All methods take `long tenantId` first (ADR-0002 §5.2).
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByEmailAsync(long tenantId, string email, CancellationToken ct = default);
    Task<User?> GetByIdAsync(long tenantId, long id, CancellationToken ct = default);
    Task<long> CreateAsync(User user, CancellationToken ct = default);
}
