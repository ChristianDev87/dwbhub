using DwbHub.Core.Messaging;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Persistence for Discord messages in bridged channels.
/// Every method takes a `long tenantId` to enforce tenant scoping at the
/// data layer (defense-in-depth alongside middleware).
/// </summary>
public interface IMessageRepository
{
    /// <summary>
    /// Idempotent insert. Uses ON CONFLICT (tenant_id, discord_message_id) DO NOTHING.
    /// Returns the inserted message, or null when the row already existed (backfill /
    /// gateway race — the caller can discard the duplicate safely).
    /// </summary>
    Task<Message?> InsertAsync(Message msg, CancellationToken ct = default);

    /// <summary>
    /// Updates content and edited_at for a Discord MessageUpdate event.
    /// Does NOT touch rows where deleted_at IS NOT NULL — edits to deleted messages
    /// are silently ignored to avoid re-surfacing soft-deleted content.
    /// Returns true iff a row was updated.
    /// </summary>
    Task<bool> ApplyEditAsync(
        long tenantId,
        long discordMessageId,
        string content,
        DateTimeOffset editedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-delete: sets deleted_at = now(). Discord does not supply a delete timestamp.
    /// Returns true iff a row was updated (false when the message was never persisted).
    /// </summary>
    Task<bool> MarkDeletedAsync(
        long tenantId,
        long discordMessageId,
        CancellationToken ct = default);

    /// <summary>
    /// Paginated history fetch for ChatPage. Returns up to <paramref name="limit"/>
    /// non-deleted messages strictly older than <paramref name="beforeSnowflake"/>
    /// (pass null to fetch the most-recent page). Ordered newest-first; the caller
    /// reverses for chronological display. Uses ix_messages_channel_time index.
    /// </summary>
    Task<IReadOnlyList<Message>> ListByChannelBeforeAsync(
        long tenantId,
        long channelId,
        long? beforeSnowflake,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch a single message by its internal autoincrement primary key.
    /// Used by test-only endpoints to resolve the internal id → discord_message_id
    /// mapping so simulated edit/delete events can target the correct row.
    /// Returns null when no row matches the supplied tenant + id combination.
    /// </summary>
    Task<Message?> GetByInternalIdAsync(long tenantId, long id, CancellationToken ct = default);
}
