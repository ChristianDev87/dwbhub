using System.Net;

namespace DwbHub.Core.Entities;

/// <summary>
/// One row of the audit_log table. PayloadJson is the raw JSONB-as-string
/// (not parsed), to keep the entity tier free of System.Text.Json deps.
/// </summary>
/// <param name="Id">Internal primary key; also the monotonic sequence used for hash chaining.</param>
/// <param name="TenantId">Owning tenant, or null for system-wide events (e.g. setup).</param>
/// <param name="ActorUserId">User who triggered the event, or null for automated/system events.</param>
/// <param name="EventType">Discriminator string (e.g. <c>"user.login"</c>) matching the canonical audit-event catalogue.</param>
/// <param name="PayloadJson">Raw JSONB payload serialised by <c>CanonicalJsonSerializer</c>. Not parsed here.</param>
/// <param name="IpAddress">Client IP at the time of the event, or null when not applicable.</param>
/// <param name="UserAgent">HTTP User-Agent at the time of the event, or null when not applicable.</param>
/// <param name="OccurredAt">Timestamp of the event (TIMESTAMPTZ).</param>
/// <param name="PrevHash">SHA-256 hash of the preceding row, or null for the first row.</param>
/// <param name="CurrentHash">SHA-256 hash of this row's payload + PrevHash, used for chain verification.</param>
public sealed record AuditLogEntry(
    long Id,
    long? TenantId,
    long? ActorUserId,
    string EventType,
    string PayloadJson,
    IPAddress? IpAddress,
    string? UserAgent,
    DateTimeOffset OccurredAt,
    byte[]? PrevHash,
    byte[] CurrentHash);
