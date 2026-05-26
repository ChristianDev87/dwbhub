namespace DwbHub.Application.Messaging;

/// <summary>
/// Discord MessageReceived translated to a Discord-agnostic shape.
/// Fired by IBotConnection when ANY message arrives in a guild channel — the
/// <see cref="IMessageService"/> filters by bridged-channel and webhook-loop before persisting.
/// </summary>
public sealed record MessageReceivedEvent
{
    public long TenantId { get; init; }
    public long GuildId { get; init; }
    public long DiscordChannelId { get; init; }
    public long DiscordMessageId { get; init; }
    public long DiscordAuthorId { get; init; }
    public string DiscordAuthorName { get; init; } = "";
    public bool AuthorIsWebhook { get; init; }
    public ulong? WebhookSourceId { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset SentAt { get; init; }
}
