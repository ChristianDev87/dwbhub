namespace DwbHub.Application.Messaging;

/// <summary>
/// Discord MessageDeleted translated to a Discord-agnostic shape.
/// Fired by IBotConnection when a guild channel message is deleted.
/// </summary>
public sealed record MessageDeletedEvent
{
    public long TenantId { get; init; }
    public long GuildId { get; init; }
    public long DiscordChannelId { get; init; }
    public long DiscordMessageId { get; init; }
}
