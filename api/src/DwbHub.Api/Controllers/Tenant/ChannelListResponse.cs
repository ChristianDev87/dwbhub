namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Response for GET /guilds/{guildPublicId}/channels.
/// Contains bridge state and optional backfill job summary per channel.
///
/// Snowflake note: DiscordChannelId is serialized as a long (JSON number).
/// For consistency with other Discord snowflake fields in the API, which also serialize as long.
/// Clients that need safe large-integer handling should
/// treat any 64-bit integer field as a string.
/// </summary>
/// <param name="Channels">Per-channel items for all channels known to this guild.</param>
public sealed record ChannelListResponse(IReadOnlyList<ChannelListItem> Channels);

/// <summary>
/// Per-channel item in the channel list response.
/// Backfill is null when no backfill job has ever been created for this channel.
///
/// N+1 note: populating Backfill for each bridged channel requires one extra
/// IChannelBackfillJobRepository.GetByChannelAsync call per bridged channel.
/// This is intentional — channels per guild are bounded (≤ hundreds) and this
/// is not a hot path. Batch loading can be added in a future task.
/// </summary>
/// <param name="PublicId">Public GUID that identifies the channel in API paths.</param>
/// <param name="DiscordChannelId">Discord channel snowflake as a long.</param>
/// <param name="Name">Channel name from Discord.</param>
/// <param name="ChannelType">Discord channel type integer.</param>
/// <param name="Position">Visual position of the channel in the guild's channel list.</param>
/// <param name="IsBridged">Whether the DwbHub webhook bridge is active for this channel.</param>
/// <param name="BridgedAt">Timestamp when the bridge was enabled, or <c>null</c> if not bridged.</param>
/// <param name="LastSyncedAt">Timestamp of the last successful channel metadata sync from Discord.</param>
/// <param name="Backfill">Most recent backfill job summary, or <c>null</c> when no job exists.</param>
public sealed record ChannelListItem(
    Guid PublicId,
    long DiscordChannelId,
    string Name,
    short ChannelType,
    int Position,
    bool IsBridged,
    DateTimeOffset? BridgedAt,
    DateTimeOffset LastSyncedAt,
    BackfillStatusItem? Backfill);

/// <summary>
/// Snapshot of the most recent backfill job for a channel.
/// Embedded in ChannelListItem and also returned standalone from
/// GET /channels/{channelPublicId}/backfill-status.
/// </summary>
/// <param name="JobId">Internal database ID of the backfill job.</param>
/// <param name="Status">Lowercased status string (e.g. <c>pending</c>, <c>running</c>, <c>completed</c>, <c>cancelled</c>).</param>
/// <param name="FetchedCount">Number of messages fetched so far.</param>
/// <param name="StartedAt">Timestamp when the job began running, or <c>null</c> if still pending.</param>
/// <param name="CompletedAt">Timestamp when the job finished or was cancelled, or <c>null</c> if still active.</param>
public sealed record BackfillStatusItem(
    long JobId,
    string Status,
    int FetchedCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
