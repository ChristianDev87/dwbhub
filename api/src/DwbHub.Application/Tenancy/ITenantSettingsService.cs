namespace DwbHub.Application.Tenancy;

/// <summary>
/// Application contract for tenant-level settings mutations.
/// Callers are responsible for validating input ranges before calling these methods.
/// </summary>
public interface ITenantSettingsService
{
    /// <summary>
    /// Updates (or resets) the per-tenant outbound-message edit window.
    /// Writes an audit event on every successful update.
    /// </summary>
    /// <param name="tenantId">Internal primary key of the tenant.</param>
    /// <param name="windowSeconds">
    /// New value in seconds, or <c>null</c> to reset to the system default.
    /// The caller must validate the range [60, 31536000] for non-null values.
    /// </param>
    /// <param name="actorUserId">User performing the change (for audit trail).</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateMessageEditWindowAsync(long tenantId, int? windowSeconds, long actorUserId, CancellationToken ct = default);
}
