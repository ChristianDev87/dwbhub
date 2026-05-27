using DwbHub.Core.Entities;
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

    /// <summary>
    /// User-facing edit: validates the 10-minute edit window, pushes the new content
    /// to Discord, persists the change and broadcasts <c>MessageUpdated</c> via SignalR.
    /// </summary>
    /// <returns>
    /// <see cref="EditMessageResult"/> discriminated union:
    /// <see cref="EditMessageResult.Success"/> on success,
    /// <see cref="EditMessageResult.NotFound"/> when the message does not exist,
    /// <see cref="EditMessageResult.Forbidden"/> when the caller is not the author,
    /// <see cref="EditMessageResult.EditWindowExpired"/> when more than 10 minutes have passed.
    /// </returns>
    Task<EditMessageResult> EditOutboundAsync(
        long tenantId,
        long channelId,
        Guid messagePublicId,
        long actorUserId,
        string newContent,
        CancellationToken ct = default);

    /// <summary>
    /// User-facing delete: hard-deletes from Discord (idempotent on 404),
    /// soft-deletes in DB and broadcasts <c>MessageDeleted</c> via SignalR.
    /// </summary>
    /// <returns>
    /// <see cref="DeleteMessageResult"/> discriminated union:
    /// <see cref="DeleteMessageResult.Success"/> on success,
    /// <see cref="DeleteMessageResult.NotFound"/> when the message does not exist (or already deleted),
    /// <see cref="DeleteMessageResult.Forbidden"/> when the caller is neither author nor Owner.
    /// </returns>
    Task<DeleteMessageResult> DeleteOutboundAsync(
        long tenantId,
        long channelId,
        Guid messagePublicId,
        long actorUserId,
        UserRole actorRole,
        CancellationToken ct = default);
}

// ── Result discriminated unions ───────────────────────────────────────────────

/// <summary>Result of <see cref="IMessageService.EditOutboundAsync"/>.</summary>
public abstract record EditMessageResult
{
    private EditMessageResult() { }

    /// <summary>Edit succeeded. Contains the updated message row.</summary>
    public sealed record Success(Message UpdatedMessage) : EditMessageResult;

    /// <summary>Message not found (wrong tenant / channel / id, or already deleted).</summary>
    public sealed record NotFound : EditMessageResult;

    /// <summary>Caller is not the original author of the message.</summary>
    public sealed record Forbidden : EditMessageResult;

    /// <summary>
    /// The 10-minute edit window has elapsed (or Discord returned 404 for the webhook message).
    /// </summary>
    public sealed record EditWindowExpired : EditMessageResult;
}

/// <summary>Result of <see cref="IMessageService.DeleteOutboundAsync"/>.</summary>
public abstract record DeleteMessageResult
{
    private DeleteMessageResult() { }

    /// <summary>Delete succeeded (Discord hard-delete + DB soft-delete).</summary>
    public sealed record Success : DeleteMessageResult;

    /// <summary>Message not found or already soft-deleted.</summary>
    public sealed record NotFound : DeleteMessageResult;

    /// <summary>Caller is neither the author nor a tenant Owner.</summary>
    public sealed record Forbidden : DeleteMessageResult;
}
