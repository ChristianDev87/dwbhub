using System.Net;

namespace DwbHub.Core.Entities;

/// <summary>
/// One row of the audit_log table. PayloadJson is the raw JSONB-as-string
/// (not parsed), to keep the entity tier free of System.Text.Json deps.
/// </summary>
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
