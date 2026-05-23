namespace DwbHub.Application.Bot;

/// <summary>
/// Per-guild Discord bot connection abstraction. Production impl wraps
/// Discord.NET's DiscordSocketClient; tests use FakeBotConnection.
///
/// Lifecycle: caller invokes ConnectAsync once, observes State via the event,
/// and calls DisconnectAsync when done. The plaintext token passed to ConnectAsync
/// is held ONLY for the duration of that call — never stored as a field.
/// </summary>
public interface IBotConnection : IAsyncDisposable
{
    long GuildId { get; }
    long TenantId { get; }
    BotConnectionState State { get; }
    DateTimeOffset? LastConnectedAt { get; }

    /// <summary>
    /// Connects with the given plaintext bot token. Initiates the gateway login
    /// and returns when the underlying client has started the connection task —
    /// typically while state is still <see cref="BotConnectionState.Connecting"/>.
    /// The transition to <see cref="BotConnectionState.Connected"/> happens
    /// asynchronously when the gateway READY event fires; subscribe to
    /// <see cref="StateChanged"/> to observe.
    /// Throws (and transitions to <see cref="BotConnectionState.TokenInvalid"/>
    /// or <see cref="BotConnectionState.Failed"/>) on connect-time errors.
    /// </summary>
    Task ConnectAsync(string plaintextToken, CancellationToken ct);

    /// <summary>
    /// Gracefully disconnects. Idempotent — calling on Disconnected is a no-op.
    /// </summary>
    Task DisconnectAsync(CancellationToken ct);

    /// <summary>
    /// Raised on every state transition. The manager subscribes to emit audit
    /// events and update guilds.last_connected_at on Connected transitions.
    /// </summary>
    event Func<BotConnectionStateChange, Task>? StateChanged;
}
