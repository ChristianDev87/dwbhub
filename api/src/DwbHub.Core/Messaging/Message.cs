namespace DwbHub.Core.Messaging;

/// <summary>
/// A persisted Discord message in a bridged channel.
/// Snowflake unique-key (tenant_id, discord_message_id) prevents duplicate inserts
/// when backfill and live gateway events race.
/// </summary>
public sealed record Message
{
    public long Id { get; init; }
    public long TenantId { get; init; }
    public long ChannelId { get; init; }                  // internal guild_channels.id
    public long DiscordMessageId { get; init; }
    public long DiscordAuthorId { get; init; }
    public string DiscordAuthorName { get; init; } = "";
    public bool ViaDwbhub { get; init; }
    public long? DwbhubUserId { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset SentAt { get; init; }           // Discord's timestamp, not now() (briefing §5)
    public DateTimeOffset? EditedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }       // soft-delete marker
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
