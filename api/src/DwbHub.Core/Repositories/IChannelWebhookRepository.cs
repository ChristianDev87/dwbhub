using DwbHub.Core.Messaging;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Persistence for encrypted Discord webhook credentials per bridged channel.
/// Every method takes a `long tenantId` to enforce tenant scoping at the
/// data layer (defense-in-depth alongside middleware).
/// The ciphertext/nonce/auth_tag fields must never be logged or returned in
/// API responses — the only legitimate consumer is IChannelWebhookCipher.Decrypt.
/// </summary>
public interface IChannelWebhookRepository
{
    /// <summary>
    /// Loads the encrypted webhook envelope for a channel. Returns null when no
    /// webhook has been registered for this channel yet.
    /// </summary>
    Task<ChannelWebhook?> GetByChannelAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a new webhook record. The UNIQUE(channel_id) constraint means only
    /// one webhook can exist per channel; callers must call DeleteByChannelAsync first
    /// when replacing an existing webhook. Throws PostgresException (SqlState 23505)
    /// on constraint violation. Returns the inserted row with its generated Id.
    /// </summary>
    Task<ChannelWebhook> InsertAsync(
        ChannelWebhook webhook,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the webhook record for a channel. Returns true iff a row was deleted.
    /// Used during channel un-bridging to remove the associated Discord webhook.
    /// </summary>
    Task<bool> DeleteByChannelAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default);
}
