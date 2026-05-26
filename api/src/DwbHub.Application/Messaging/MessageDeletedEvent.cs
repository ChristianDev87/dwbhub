namespace DwbHub.Application.Messaging;

/// <summary>
/// Reason a message was deleted.
/// </summary>
public enum MessageDeletedReason
{
    /// <summary>Message was deleted on the Discord side (gateway MessageDeleted event).</summary>
    DiscordSide,
    /// <summary>Message was deleted by its own author via DwbHub.</summary>
    Self,
    /// <summary>Outbound (webhook) message was moderation-deleted by an Owner.</summary>
    ModerationOutbound,
    /// <summary>Inbound (Discord-user) message was moderation-deleted by an Owner via the bot REST API.</summary>
    ModerationInbound,
}

/// <summary>
/// Discord MessageDeleted translated to a Discord-agnostic shape.
/// Fired by IBotConnection when a guild channel message is deleted (Reason = DiscordSide),
/// or constructed directly by MessageService.DeleteAsync for user-initiated deletes.
/// </summary>
public sealed record MessageDeletedEvent
{
    public long TenantId { get; init; }
    public long GuildId { get; init; }
    public long DiscordChannelId { get; init; }
    public long DiscordMessageId { get; init; }

    /// <summary>
    /// Internal DwbHub user ID of the actor who triggered the delete.
    /// <c>null</c> when the delete originated on the Discord side (Reason = DiscordSide).
    /// </summary>
    public long? DeletedByUserId { get; init; }

    /// <summary>
    /// Why the message was deleted. Defaults to <see cref="MessageDeletedReason.DiscordSide"/>
    /// to keep existing construction sites backward-compatible.
    /// </summary>
    public MessageDeletedReason Reason { get; init; } = MessageDeletedReason.DiscordSide;
}
