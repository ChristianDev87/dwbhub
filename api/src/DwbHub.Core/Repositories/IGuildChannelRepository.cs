using DwbHub.Core.Messaging;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Persistence for Discord channels known per guild.
/// Every method takes a `long tenantId` to enforce tenant scoping at the
/// data layer (defense-in-depth alongside middleware).
/// </summary>
public interface IGuildChannelRepository
{
    /// <summary>
    /// Loads a channel by its public UUID, tenant-scoped. Returns null when the
    /// UUID does not exist or belongs to a different tenant (info-leak protection).
    /// </summary>
    Task<GuildChannel?> GetByPublicIdAsync(
        long tenantId,
        Guid publicId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads a channel by its Discord snowflake ID, tenant-scoped. Returns null
    /// when the channel has not yet been synced into the system.
    /// </summary>
    Task<GuildChannel?> GetByDiscordIdAsync(
        long tenantId,
        long discordChannelId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all channels for a guild, ordered by position ASC. No paging — guilds
    /// have at most a few hundred channels in practice.
    /// </summary>
    Task<IReadOnlyList<GuildChannel>> ListByGuildAsync(
        long tenantId,
        long guildId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all channels where is_bridged = true across all guilds for a tenant.
    /// Used by the message event handlers to filter irrelevant guild-channel events.
    /// Uses ix_guild_channels_bridged partial index.
    /// </summary>
    Task<IReadOnlyList<GuildChannel>> ListBridgedAsync(
        long tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Upsert a channel observed during a Discord channel-list sync.
    /// Inserts when (tenant_id, discord_channel_id) is new; updates name, channel_type,
    /// position, and last_synced_at otherwise.
    /// Does NOT touch is_bridged or bridged_at — those are owner-managed.
    /// Returns the current row (inserted or updated).
    /// </summary>
    Task<GuildChannel> UpsertFromSyncAsync(
        long tenantId,
        long guildId,
        long discordChannelId,
        string name,
        short channelType,
        int position,
        CancellationToken ct = default);

    /// <summary>
    /// Toggles is_bridged for a channel identified by its public UUID. When bridging
    /// (isBridged=true), sets bridged_at = now() only if bridged_at IS NULL
    /// (i.e., does not reset bridged_at on re-toggle — preserves first-bridge timestamp).
    /// Returns true iff a row was updated.
    /// </summary>
    Task<bool> SetBridgedAsync(
        long tenantId,
        Guid publicId,
        bool isBridged,
        CancellationToken ct = default);
}
