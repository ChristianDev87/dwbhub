using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Access to the singleton bootstrap-lock row. The provisioner (Plan 0.3d
/// Task 6) and the setup endpoint (Task 9) are the only consumers.
/// </summary>
public interface ISystemBootstrapLockRepository
{
    /// <summary>
    /// Load the single bootstrap-lock row, or null if none exists.
    /// </summary>
    Task<SystemBootstrapLock?> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Insert a fresh lock row. Throws on UNIQUE-violation if a row already
    /// exists (defense in depth — the provisioner checks LoadAsync first).
    /// </summary>
    Task InsertAsync(byte[] tokenHash, CancellationToken ct = default);

    /// <summary>
    /// Atomically consume the lock — sets consumed_at + consumed_by_user_id
    /// only if the row's token_hash matches AND consumed_at IS NULL.
    /// Returns true if exactly one row was updated.
    /// </summary>
    Task<bool> TryConsumeAsync(byte[] tokenHash, long consumedByUserId, CancellationToken ct = default);
}
