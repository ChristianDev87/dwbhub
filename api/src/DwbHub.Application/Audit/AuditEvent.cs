using System.Net;

namespace DwbHub.Application.Audit;

/// <summary>
/// Input record for IAuditWriter.RecordAsync. Payload is an alphabetically-key-sortable
/// dictionary; values become canonical JSON during serialize. Payload must not contain
/// passwords, refresh-token plaintexts, or other secrets — see spec §3.4 PII discipline.
/// </summary>
/// <param name="TenantId">Tenant scope, or <c>null</c> for system-level events.</param>
/// <param name="ActorUserId">User who triggered the event, or <c>null</c> for automated/anonymous actions.</param>
/// <param name="EventType">Dot-separated event identifier, e.g. <c>auth.login.success</c>.</param>
/// <param name="Payload">Structured event context. Must not contain secrets.</param>
/// <param name="IpAddress">Client IP address for network-origin context, or <c>null</c> when unavailable.</param>
/// <param name="UserAgent">HTTP User-Agent string for additional origin context, or <c>null</c>.</param>
public sealed record AuditEvent(
    long? TenantId,
    long? ActorUserId,
    string EventType,
    IReadOnlyDictionary<string, object?> Payload,
    IPAddress? IpAddress = null,
    string? UserAgent = null);
