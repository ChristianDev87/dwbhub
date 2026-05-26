using System.Net;
using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Append-only audit log storage. Rows are hash-chained (each row's hash covers
/// its payload plus the previous row's hash) so any tampering or gap is detectable
/// by <see cref="IAuditVerifyStateRepository"/> during the integrity-check job.
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>
    /// Inserts one audit row, hash-chained to the previous tip. Takes
    /// payloadHash precomputed by the application (see CanonicalJsonSerializer.HashEvent).
    /// SERIALIZES concurrent inserts via pg_advisory_xact_lock(7341). Throws on any DB error.
    /// </summary>
    Task<(long Id, byte[] CurrentHash)> InsertAsync(
        long? tenantId,
        long? actorUserId,
        string eventType,
        string payloadJson,
        byte[] payloadHash,
        DateTimeOffset occurredAt,
        IPAddress? ipAddress,
        string? userAgent,
        CancellationToken ct = default);

    /// <summary>
    /// Streams audit_log rows ordered by id ASC, optionally starting after a given id.
    /// Returned as an async-enumerable so the caller can re-hash row-by-row without
    /// materializing the whole table. Use startAfterId=0 for a full walk.
    /// </summary>
    IAsyncEnumerable<AuditLogEntry> StreamAscAsync(
        long startAfterId,
        CancellationToken ct = default);
}
