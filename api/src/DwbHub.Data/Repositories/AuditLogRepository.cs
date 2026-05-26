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

/// <summary>
/// Dapper-backed implementation of <see cref="IAuditLogRepository"/>.
/// </summary>
public sealed class AuditLogRepository(IDbConnectionFactory connectionFactory) : IAuditLogRepository
{
    /// <inheritdoc/>
    /// <remarks>
    /// Acquires <c>pg_advisory_xact_lock(7341)</c> inside an explicit transaction
    /// so concurrent inserts are serialised and the prev_hash always points to the
    /// current chain tip. The stored hash is derived from the JSONB text as
    /// persisted by PostgreSQL — not from the C#-serialised form — so chain
    /// verification reproduces the same value when re-reading the column.
    /// </remarks>
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
        //
        // The inner payload hash is computed from the STORED JSONB text (payload_json::text)
        // so that chain verification — which reads payload_json::text back — reproduces the
        // same hash. This keeps the hash canonical with respect to PostgreSQL's JSONB output
        // rather than the C#-serialized compact form. The payloadHash C# argument is accepted
        // for interface compatibility but the actual stored hash is always derived from
        // what PostgreSQL persists.
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
                           || digest(
                               '{"actorUserId":'  || COALESCE(@ActorUserId::text,   'null') ||
                               ',"eventType":'    || to_json(@EventType::text)               ||
                               ',"ipAddress":'    || CASE WHEN @IpAddress IS NULL THEN 'null'
                                                         ELSE to_json(host(@IpAddress::inet)) END ||
                               ',"occurredAt":'   || to_json(to_char(@OccurredAt AT TIME ZONE 'UTC',
                                                       'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'))    ||
                               ',"payloadJson":'  || to_json(@PayloadJson::jsonb::text)       ||
                               ',"tenantId":'     || COALESCE(@TenantId::text,     'null') ||
                               ',"userAgent":'    || COALESCE(to_json(@UserAgent::text), 'null') ||
                               '}',
                               'sha256'),
                           'sha256')
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

    /// <inheritdoc/>
    public async IAsyncEnumerable<AuditLogEntry> StreamAscAsync(
        long startAfterId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        var npgsqlConn = (NpgsqlConnection)conn;
        const string sql = """
            SELECT id, tenant_id, actor_user_id, event_type,
                   payload_json::text AS payload_json,
                   host(ip_address) AS ip_address, user_agent,
                   occurred_at, prev_hash, current_hash
            FROM audit_log
            WHERE id > @StartAfterId
            ORDER BY id ASC
            """;
        await using var cmd = new NpgsqlCommand(sql, npgsqlConn);
        cmd.Parameters.AddWithValue("StartAfterId", startAfterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var ipStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            yield return new AuditLogEntry(
                Id: reader.GetInt64(0),
                TenantId: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                ActorUserId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                EventType: reader.GetString(3),
                PayloadJson: reader.GetString(4),
                IpAddress: ipStr is null ? null : System.Net.IPAddress.Parse(ipStr),
                UserAgent: reader.IsDBNull(6) ? null : reader.GetString(6),
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(7),
                PrevHash: reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8),
                CurrentHash: (byte[])reader.GetValue(9));
        }
    }
}
