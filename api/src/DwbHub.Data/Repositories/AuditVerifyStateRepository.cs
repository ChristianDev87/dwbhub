using System.Data;
using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

// DWBHUB-NO-TENANT-FILTER: singleton state row; allowlisted in
// tools/check-tenant-filter.ps1 in Task 13.
namespace DwbHub.Data.Repositories;

/// <summary>
/// Dapper-backed implementation of <see cref="IAuditVerifyStateRepository"/>.
/// </summary>
public sealed class AuditVerifyStateRepository : IAuditVerifyStateRepository
{
    /// <inheritdoc/>
    public async Task<AuditVerifyState> LoadForUpdateAsync(
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        const string sql = """
            SELECT last_verified_id, last_verified_hash, last_run_at, last_run_status
            FROM audit_verify_state
            WHERE id = 1
            FOR UPDATE
            """;
        return await conn.QuerySingleAsync<AuditVerifyState>(
            new CommandDefinition(sql, transaction: tx, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateProgressAsync(
        IDbConnection conn, IDbTransaction tx,
        long lastVerifiedId, byte[] lastVerifiedHash,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE audit_verify_state
            SET last_verified_id = @LastId,
                last_verified_hash = @LastHash,
                last_run_at = now(),
                last_run_status = 'ok'
            WHERE id = 1
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { LastId = lastVerifiedId, LastHash = lastVerifiedHash },
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task MarkBrokenAsync(
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE audit_verify_state
            SET last_run_at = now(),
                last_run_status = 'broken'
            WHERE id = 1
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql,
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
    }
}
