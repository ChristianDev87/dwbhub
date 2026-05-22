using System.Net;

namespace DwbHub.Application.Audit;

/// <summary>
/// Input record for IAuditWriter.RecordAsync. Payload is an alphabetically-key-sortable
/// dictionary; values become canonical JSON during serialize. Payload MUST NOT contain
/// passwords, refresh-token plaintexts, or other secrets — see spec §3.4 PII discipline.
/// </summary>
public sealed record AuditEvent(
    long? TenantId,
    long? ActorUserId,
    string EventType,
    IReadOnlyDictionary<string, object?> Payload,
    IPAddress? IpAddress = null,
    string? UserAgent = null);
