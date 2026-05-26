namespace DwbHub.Application.Messaging;

public sealed record MessageDeletedEvent
{
    public long TenantId { get; init; }
    public long GuildId { get; init; }
    public long DiscordChannelId { get; init; }
    public long DiscordMessageId { get; init; }
}
