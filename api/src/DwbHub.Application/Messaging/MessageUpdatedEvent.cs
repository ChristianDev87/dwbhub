namespace DwbHub.Application.Messaging;

/// <summary>
/// Discord MessageUpdated translated to a Discord-agnostic shape.
/// Fired by IBotConnection when a guild channel message is edited; embed-resolution
/// non-edits (no <c>EditedTimestamp</c> change) are suppressed at the source.
/// </summary>
public sealed record MessageUpdatedEvent
{
    public long TenantId { get; init; }
    public long GuildId { get; init; }
    public long DiscordChannelId { get; init; }
    public long DiscordMessageId { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset EditedAt { get; init; }
}
