using DwbHub.Api.Hubs;
using DwbHub.Application.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace DwbHub.Api.Messaging;

/// <summary>
/// Implements <see cref="IMessagesBroadcaster"/> using
/// <see cref="IHubContext{MessagesHub}"/>.
///
/// Placement note: this class lives in DwbHub.Api (not DwbHub.Infrastructure)
/// because IHubContext&lt;THub&gt; requires the concrete Hub type <see cref="MessagesHub"/>,
/// which is declared in DwbHub.Api. Infrastructure does not reference Api
/// (doing so would create a circular dependency: Api → Infrastructure → Api).
/// The DI contract (IMessagesBroadcaster) remains in DwbHub.Application so
/// all Application services stay free of SignalR types.
///
/// Security-critical invariant: tenant group names are derived entirely from
/// server-supplied TenantId parameters or DTO fields. No caller-supplied
/// group name is ever accepted. This prevents a compromised caller from
/// routing events to another tenant's clients.
/// </summary>
public sealed class SignalRMessagesBroadcaster : IMessagesBroadcaster
{
    private readonly IHubContext<MessagesHub> _hub;

    public SignalRMessagesBroadcaster(IHubContext<MessagesHub> hub)
    {
        _hub = hub;
    }

    // Group name is derived solely from server-supplied tenantId — never from client input.
    private static string Group(long tenantId) => $"tenant:{tenantId}";

    /// <inheritdoc />
    public Task MessageReceivedAsync(
        MessageBroadcastDto msg,
        Guid channelPublicId,
        CancellationToken ct = default)
        => _hub.Clients.Group(Group(msg.TenantId)).SendAsync("MessageReceived", msg, ct);

    /// <inheritdoc />
    public Task MessageUpdatedAsync(
        long tenantId,
        long messageId,
        string content,
        DateTimeOffset editedAt,
        CancellationToken ct = default)
        => _hub.Clients.Group(Group(tenantId)).SendAsync(
            "MessageUpdated",
            new { messageId, content, editedAt },
            ct);

    /// <inheritdoc />
    public Task MessageDeletedAsync(
        long tenantId,
        long messageId,
        CancellationToken ct = default)
        => _hub.Clients.Group(Group(tenantId)).SendAsync(
            "MessageDeleted",
            new { messageId },
            ct);

    /// <inheritdoc />
    public Task BackfillProgressAsync(
        long tenantId,
        Guid channelPublicId,
        long jobId,
        int fetchedCount,
        CancellationToken ct = default)
        => _hub.Clients.Group(Group(tenantId)).SendAsync(
            "BackfillProgress",
            new { channelPublicId, jobId, fetchedCount },
            ct);

    /// <inheritdoc />
    public Task BackfillCompleteAsync(
        long tenantId,
        Guid channelPublicId,
        long jobId,
        int fetchedCount,
        CancellationToken ct = default)
        => _hub.Clients.Group(Group(tenantId)).SendAsync(
            "BackfillComplete",
            new { channelPublicId, jobId, fetchedCount },
            ct);

    /// <inheritdoc />
    public Task ChannelBridgeChangedAsync(
        long tenantId,
        Guid channelPublicId,
        bool isBridged,
        CancellationToken ct = default)
        => _hub.Clients.Group(Group(tenantId)).SendAsync(
            "ChannelBridgeChanged",
            new { channelPublicId, isBridged },
            ct);
}
