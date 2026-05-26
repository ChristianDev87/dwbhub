using DwbHub.Core.Repositories;

namespace DwbHub.Application.Audit;

/// <summary>
/// Serializes and persists audit events using the canonical JSON format and SHA-256 hash chain.
/// </summary>
public sealed class AuditWriter(IAuditLogRepository repo) : IAuditWriter
{
    /// <inheritdoc/>
    public async Task<long> RecordAsync(AuditEvent evt, CancellationToken ct = default)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var payloadJson = CanonicalJsonSerializer.Serialize(evt.Payload);
        var payloadHash = CanonicalJsonSerializer.HashEvent(
            evt.TenantId, evt.ActorUserId, evt.EventType,
            payloadJson, evt.IpAddress, evt.UserAgent, occurredAt);

        var (id, _) = await repo.InsertAsync(
            evt.TenantId, evt.ActorUserId, evt.EventType,
            payloadJson, payloadHash, occurredAt,
            evt.IpAddress, evt.UserAgent, ct).ConfigureAwait(false);
        return id;
    }
}
