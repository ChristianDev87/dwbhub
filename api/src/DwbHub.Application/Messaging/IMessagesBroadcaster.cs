namespace DwbHub.Application.Messaging;

/// <summary>
/// Abstraction over SignalR that lets Application services broadcast to connected
/// tenant clients without taking a dependency on SignalR types.
/// The SignalR implementation is <c>SignalRMessagesBroadcaster</c>.
/// Every method is scoped by <c>TenantId</c> — derived from server-side context
/// only, never from client input.
/// </summary>
public interface IMessagesBroadcaster
{
    /// <summary>Broadcast a newly persisted message to all SignalR clients subscribed to <paramref name="channelPublicId"/>.</summary>
    Task MessageReceivedAsync(MessageBroadcastDto msg, Guid channelPublicId, CancellationToken ct = default);
    /// <summary>Notify clients that the content of an existing message has changed.</summary>
    Task MessageUpdatedAsync(long tenantId, long messageId, string content, DateTimeOffset editedAt, CancellationToken ct = default);
    /// <summary>Notify clients that a message has been soft-deleted.</summary>
    Task MessageDeletedAsync(long tenantId, long messageId, CancellationToken ct = default);
    /// <summary>Push an intermediate backfill progress update so clients can display a live count.</summary>
    Task BackfillProgressAsync(long tenantId, Guid channelPublicId, long jobId, int fetchedCount, CancellationToken ct = default);
    /// <summary>Notify clients that a backfill job has finished and the final fetched count is available.</summary>
    Task BackfillCompleteAsync(long tenantId, Guid channelPublicId, long jobId, int fetchedCount, CancellationToken ct = default);
    /// <summary>Notify clients that a channel's bridge status has changed (bridged or un-bridged).</summary>
    Task ChannelBridgeChangedAsync(long tenantId, Guid channelPublicId, bool isBridged, CancellationToken ct = default);
}

/// <summary>
/// Projection of a persisted <see cref="DwbHub.Core.Messaging.Message"/> suitable
/// for pushing to SignalR clients. Does not include ciphertext fields or internal IDs.
/// </summary>
public sealed record MessageBroadcastDto(
    long Id,
    long TenantId,
    Guid ChannelPublicId,
    string AuthorName,
    string Content,
    DateTimeOffset SentAt,
    bool ViaDwbhub,
    long DiscordMessageId);
