namespace DwbHub.Application.Tenancy;

public interface ITenantSettingsService
{
    /// <summary>
    /// Update the per-tenant message-edit-window setting. <paramref name="windowSeconds"/> must be
    /// either NULL (= unlimited) or between 60 and 31536000 seconds. Throws
    /// <see cref="ArgumentOutOfRangeException"/> on out-of-range values.
    /// </summary>
    Task UpdateMessageEditWindowAsync(long tenantId, int? windowSeconds, long actorUserId, CancellationToken ct);
}
