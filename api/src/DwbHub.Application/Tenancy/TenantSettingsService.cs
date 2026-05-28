using DwbHub.Application.Audit;
using DwbHub.Core.Repositories;

namespace DwbHub.Application.Tenancy;

/// <summary>
/// Implements <see cref="ITenantSettingsService"/> by delegating to
/// <see cref="ITenantRepository"/> and writing structured audit events.
/// </summary>
public sealed class TenantSettingsService(
    ITenantRepository tenants,
    IAuditWriter audit) : ITenantSettingsService
{
    /// <inheritdoc/>
    public async Task UpdateMessageEditWindowAsync(
        long tenantId,
        int? windowSeconds,
        long actorUserId,
        CancellationToken ct = default)
    {
        await tenants.UpdateMessageEditWindowAsync(tenantId, windowSeconds, ct)
            .ConfigureAwait(false);

        await audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: actorUserId,
            EventType: AuditEventTypes.TenantSettingsMessageEditWindowUpdated,
            Payload: new Dictionary<string, object?>
            {
                ["windowSeconds"] = windowSeconds,
            }),
            ct).ConfigureAwait(false);
    }
}
