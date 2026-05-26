namespace DwbHub.Application.Messaging;

/// <summary>
/// Abstraction over SignalR that lets Application services broadcast to connected
/// tenant clients without taking a dependency on SignalR types.
/// Implementation (<c>SignalRMessagesBroadcaster</c>) lives in Task 8.
/// Every method is scoped by <c>TenantId</c> — derived from server-side context
/// only, never from client input.
/// </summary>
public interface IMessagesBroadcaster
{
    Task MessageReceivedAsync(MessageBroadcastDto msg, Guid channelPublicId, CancellationToken ct = default);
    Task MessageUpdatedAsync(long tenantId, long messageId, string content, DateTimeOffset editedAt, CancellationToken ct = default);
    Task MessageDeletedAsync(long tenantId, long messageId, CancellationToken ct = default);
    Task BackfillProgressAsync(long tenantId, Guid channelPublicId, long jobId, int fetchedCount, CancellationToken ct = default);
    Task BackfillCompleteAsync(long tenantId, Guid channelPublicId, long jobId, int fetchedCount, CancellationToken ct = default);
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
