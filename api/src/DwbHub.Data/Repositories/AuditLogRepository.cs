using System.Data;
using System.Net;
using System.Runtime.CompilerServices;
using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using Npgsql;

// DWBHUB-NO-TENANT-FILTER: audit_log spans tenants by design — see migration 008
// header + spec §3.2. Allowlisted in tools/check-tenant-filter.ps1 in Task 13.
namespace DwbHub.Data.Repositories;

public sealed class AuditLogRepository(IDbConnectionFactory connectionFactory) : IAuditLogRepository
{
    public async Task<(long Id, byte[] CurrentHash)> InsertAsync(
        long? tenantId, long? actorUserId, string eventType,
        string payloadJson, byte[] payloadHash, DateTimeOffset occurredAt,
        IPAddress? ipAddress, string? userAgent,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // Single-CTE pattern: advisory lock + chain-tip lookup + hash compute + insert,
        // all in one round-trip. The advisory lock is transaction-scoped and serializes
        // concurrent inserts so the prev_hash always matches the current chain tip.
        // We open an explicit transaction so pg_advisory_xact_lock is held until commit.
        // QueryMultipleAsync reads both resultsets; we skip the void lock result and
        // read the INSERT RETURNING row from the second grid.
        using var txn = conn.BeginTransaction();
        const string sql = """
            SELECT pg_advisory_xact_lock(7341);

            WITH last AS (
                SELECT current_hash FROM audit_log ORDER BY id DESC LIMIT 1
            ),
            ins AS (
                INSERT INTO audit_log
                    (tenant_id, actor_user_id, event_type, payload_json,
                     ip_address, user_agent, occurred_at, prev_hash, current_hash)
                SELECT @TenantId, @ActorUserId, @EventType, @PayloadJson::jsonb,
                       @IpAddress::inet, @UserAgent, @OccurredAt,
                       (SELECT current_hash FROM last),
                       digest(
                           COALESCE((SELECT current_hash FROM last), '\x'::bytea)
                           || @PayloadHash, 'sha256')
                RETURNING id, current_hash
            )
            SELECT id, current_hash FROM ins;
            """;

        var parameters = new
        {
            TenantId = tenantId,
            ActorUserId = actorUserId,
            EventType = eventType,
            PayloadJson = payloadJson,
            PayloadHash = payloadHash,
            OccurredAt = occurredAt,
            IpAddress = ipAddress?.ToString(),
            UserAgent = userAgent,
        };

        using var multi = await conn.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, transaction: txn, cancellationToken: ct))
            .ConfigureAwait(false);

        // First grid: pg_advisory_xact_lock returns void — read and discard.
        await multi.ReadAsync().ConfigureAwait(false);
        // Second grid: INSERT RETURNING id, current_hash.
        var row = await multi.ReadSingleAsync<(long Id, byte[] CurrentHash)>()
            .ConfigureAwait(false);

        txn.Commit();
        return row;
    }

    public IAsyncEnumerable<AuditLogEntry> StreamAscAsync(
        long startAfterId, CancellationToken ct = default)
        => throw new NotImplementedException("StreamAscAsync is implemented in Task 7.");
}
