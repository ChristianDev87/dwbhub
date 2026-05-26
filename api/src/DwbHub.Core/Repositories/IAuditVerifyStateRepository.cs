using System.Data;
using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Persistence for the singleton audit-verify state row. Tracks the progress
/// Tracks the progress of the hash-chain integrity-check background job. All methods
/// require the caller to hold an open Npgsql transaction so the SELECT FOR UPDATE
/// and subsequent UPDATE are atomic.
/// </summary>
public interface IAuditVerifyStateRepository
{
    /// <summary>
    /// Loads and FOR-UPDATE-locks the singleton row. The caller must hold an
    /// open Npgsql transaction; the lock is released on COMMIT/ROLLBACK.
    /// </summary>
    Task<AuditVerifyState> LoadForUpdateAsync(
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    /// <summary>Updates state-row after a successful verify run. last_run_status := 'ok'.</summary>
    Task UpdateProgressAsync(
        IDbConnection conn, IDbTransaction tx,
        long lastVerifiedId, byte[] lastVerifiedHash,
        CancellationToken ct = default);

    /// <summary>Marks the most recent run as broken. Leaves last_verified_id untouched.</summary>
    Task MarkBrokenAsync(
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);
}
