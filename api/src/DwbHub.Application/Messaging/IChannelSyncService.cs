namespace DwbHub.Application.Messaging;

/// <summary>
/// Syncs the list of Discord channels for a guild into the local database.
/// The sync is non-destructive: channels absent from the Discord response are left
/// alone (they may be private channels the bot cannot see). Only name, position,
/// channel_type, and last_synced_at are updated — never is_bridged or bridged_at.
/// </summary>
public interface IChannelSyncService
{
    /// <summary>
    /// Fetches all channels from Discord for the guild identified by
    /// <paramref name="guildPublicId"/>, upserting each row into guild_channels.
    /// Emits a <c>channel_sync_completed</c> audit event on success.
    /// </summary>
    Task SyncFromDiscordAsync(
        long tenantId,
        Guid guildPublicId,
        CancellationToken ct = default);
}
