namespace DwbHub.Application.Messaging;

/// <summary>
/// Manages the lifecycle of Discord webhook credentials for bridged channels.
///
/// Security invariant: the raw webhook token is NEVER returned to callers or
/// exposed in any DTO or log line. It lives in memory only during the outbound
/// HTTP call to Discord and is immediately discarded.
/// </summary>
public interface IChannelWebhookService
{
    /// <summary>
    /// Creates a Discord webhook for the given channel, encrypts the token via
    /// AES-256-GCM, and inserts the envelope into channel_webhooks.
    ///
    /// On DB insert failure after a successful Discord POST the Discord webhook
    /// is deleted (rollback) to prevent orphaned webhooks.
    /// </summary>
    Task CreateForChannelAsync(
        long tenantId,
        long channelId,
        long userId,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the Discord webhook and removes the corresponding DB row.
    /// Discord 404 (already deleted) is treated as success (idempotent).
    /// Discord 403/401 (permissions lost) is logged and DB cleanup continues.
    /// </summary>
    Task DeleteForChannelAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default);

    /// <summary>
    /// Decrypts the stored webhook token for internal callers.
    /// This method MUST NOT be called from any HTTP controller or DTO projection.
    /// </summary>
    Task<string> DecryptTokenAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default);
}
