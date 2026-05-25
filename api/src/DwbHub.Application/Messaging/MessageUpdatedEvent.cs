namespace DwbHub.Application.Messaging;

public sealed record MessageUpdatedEvent
{
    public long TenantId { get; init; }
    public long GuildId { get; init; }
    public long DiscordChannelId { get; init; }
    public long DiscordMessageId { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset EditedAt { get; init; }
}
