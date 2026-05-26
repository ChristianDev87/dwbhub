namespace DwbHub.Application.Tenancy;

using DwbHub.Application.Audit;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

public sealed class TenantSettingsService(
    ITenantRepository tenants,
    IAuditWriter audit,
    ILogger<TenantSettingsService> logger) : ITenantSettingsService
{
    public async Task UpdateMessageEditWindowAsync(long tenantId, int? windowSeconds, long actorUserId, CancellationToken ct)
    {
        if (windowSeconds is int w && (w < 60 || w > 31536000))
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), "Must be between 60 and 31536000 or null");

        await tenants.UpdateMessageEditWindowAsync(tenantId, windowSeconds, ct).ConfigureAwait(false);

        await audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: actorUserId,
            EventType: AuditEventTypes.TenantSettingsMessageEditWindowUpdated,
            Payload: new Dictionary<string, object?> { ["windowSeconds"] = windowSeconds },
            IpAddress: null, UserAgent: null), ct).ConfigureAwait(false);

        logger.LogInformation("Tenant {TenantId} message edit window updated to {WindowSeconds}s by user {ActorUserId}",
            tenantId, windowSeconds, actorUserId);
    }
}
