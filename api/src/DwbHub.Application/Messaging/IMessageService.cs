using DwbHub.Core.Messaging;

namespace DwbHub.Application.Messaging;

/// <summary>
/// Application service that handles all Discord message lifecycle events and
/// outbound message sending.
/// </summary>
public interface IMessageService
{
    /// <summary>
    /// Persist an inbound Discord message. Applies all three filter guards before
    /// inserting. Returns the persisted row, or null when the message was filtered
    /// out or was a duplicate snowflake.
    /// </summary>
    Task<Message?> PersistInboundAsync(MessageReceivedEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Update an existing message's content when Discord fires a MessageUpdated event.
    /// </summary>
    Task PersistEditAsync(MessageUpdatedEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Soft-delete a message when Discord fires a MessageDeleted event.
    /// </summary>
    Task MarkDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Send a message via the channel's registered Discord webhook.
    /// On Discord 404 (webhook gone): recreates the webhook and retries once.
    /// Persists with <c>via_dwbhub=true</c> and <c>dwbhub_user_id=userId</c>.
    /// </summary>
    Task<Message> SendOutboundAsync(
        long tenantId,
        long channelId,
        long userId,
        string displayName,
        string content,
        CancellationToken ct = default);

    /// <summary>
    /// Paginated history fetch for the ChatPage. Delegates to
    /// <see cref="DwbHub.Core.Repositories.IMessageRepository.ListByChannelBeforeAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Message>> ListHistoryAsync(
        long tenantId,
        long channelId,
        long? beforeSnowflake,
        int limit,
        CancellationToken ct = default);
}
