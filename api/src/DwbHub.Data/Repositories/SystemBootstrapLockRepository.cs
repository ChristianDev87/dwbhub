using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

// DWBHUB-NO-TENANT-FILTER: system_bootstrap_lock is allowlisted in
// tools/check-tenant-filter.ps1 (line 33) — it's a deployment-scoped singleton
// table, not per-tenant. Lookups by id or token_hash are exhaustive on their own.
namespace DwbHub.Data.Repositories;

public sealed class SystemBootstrapLockRepository(IDbConnectionFactory connectionFactory) : ISystemBootstrapLockRepository
{
    public async Task<SystemBootstrapLock?> LoadAsync(CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, token_hash, issued_at, consumed_at, consumed_by_user_id
            FROM system_bootstrap_lock
            ORDER BY id ASC
            LIMIT 1;
            """;
        return await conn.QuerySingleOrDefaultAsync<SystemBootstrapLock>(
            new CommandDefinition(sql, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task InsertAsync(byte[] tokenHash, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO system_bootstrap_lock (token_hash)
            VALUES (@TokenHash);
            """;
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { TokenHash = tokenHash }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<bool> TryConsumeAsync(byte[] tokenHash, long consumedByUserId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE system_bootstrap_lock
            SET consumed_at = now(),
                consumed_by_user_id = @UserId
            WHERE token_hash = @TokenHash
              AND consumed_at IS NULL;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { TokenHash = tokenHash, UserId = consumedByUserId }, cancellationToken: ct))
            .ConfigureAwait(false);
        return affected == 1;
    }
}
