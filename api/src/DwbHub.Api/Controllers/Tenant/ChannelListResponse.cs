namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Response for GET /guilds/{guildPublicId}/channels.
/// Contains bridge state and optional backfill job summary per channel.
///
/// Snowflake note: DiscordChannelId is serialized as a long (JSON number).
/// For consistency with MessageHistoryItem (Task 9) which also uses long for
/// Discord snowflakes. Clients that need safe large-integer handling should
/// treat any 64-bit integer field as a string.
/// </summary>
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
public sealed record BackfillStatusItem(
    long JobId,
    string Status,
    int FetchedCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
