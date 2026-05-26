using DwbHub.Application.Bot;
using DwbHub.Application.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Bot;

/// <summary>
/// In-process stub for <see cref="IBotConnection"/> used when
/// <c>DWBHUB_DISCORD_TEST_MODE=fake-rest</c> is set. Does not talk to Discord
/// — it just transitions through Connecting → Connected synthetically so the
/// rest of the stack can exercise the bot lifecycle without burning real
/// Discord API quota or risking a 401-loop ban when test tokens are used.
/// </summary>
/// <remarks>
/// Throws at construction if the hosting environment is Production
/// (defence-in-depth — Program.cs already guards on the env var, but a
/// runtime crash on startup is the right behaviour if that guard is ever
/// bypassed).
/// </remarks>
public sealed class FakeBotConnection : IBotConnection, IAsyncDisposable
{
    private readonly ILogger<FakeBotConnection> _logger;
    private readonly object _stateLock = new();
    private BotConnectionState _state = BotConnectionState.Disconnected;
    private DateTimeOffset? _lastConnectedAt;
    private bool _disposed;

    public FakeBotConnection(long guildId, long tenantId, IHostEnvironment env, ILogger<FakeBotConnection> logger)
    {
        if (env.IsProduction())
            throw new InvalidOperationException(
                "FakeBotConnection must NEVER be activated in Production. " +
                "Check DWBHUB_DISCORD_TEST_MODE env var is unset on the production stack.");
        GuildId = guildId;
        TenantId = tenantId;
        _logger = logger;
    }

    /// <inheritdoc/>
    public long GuildId { get; }
    /// <inheritdoc/>
    public long TenantId { get; }
    /// <inheritdoc/>
    public BotConnectionState State { get { lock (_stateLock) return _state; } }
    /// <inheritdoc/>
    public DateTimeOffset? LastConnectedAt { get { lock (_stateLock) return _lastConnectedAt; } }

    /// <inheritdoc/>
    public event Func<BotConnectionStateChange, Task>? StateChanged;
    // The message events are part of IBotConnection but FakeBotConnection
    // never fires them — there are no real Discord gateway events in test mode.
    // The interface still requires the declaration; suppress CS0067.
#pragma warning disable CS0067
    /// <inheritdoc/>
    public event Func<MessageReceivedEvent, Task>? MessageReceived;
    /// <inheritdoc/>
    public event Func<MessageUpdatedEvent, Task>? MessageUpdated;
    /// <inheritdoc/>
    public event Func<MessageDeletedEvent, Task>? MessageDeleted;
#pragma warning restore CS0067

    /// <inheritdoc/>
    public async Task ConnectAsync(string botToken, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FakeBotConnection));
        await TransitionAsync(BotConnectionState.Connecting, null).ConfigureAwait(false);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        lock (_stateLock) { _lastConnectedAt = DateTimeOffset.UtcNow; }
        await TransitionAsync(BotConnectionState.Connected, null).ConfigureAwait(false);
        _logger.LogInformation("FakeBotConnection: synthetic CONNECT for guild {GuildId}", GuildId);
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (_disposed) return;
        if (State == BotConnectionState.Disconnected) return;
        await TransitionAsync(BotConnectionState.Disconnected, null).ConfigureAwait(false);
        _logger.LogInformation("FakeBotConnection: synthetic DISCONNECT for guild {GuildId}", GuildId);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task TransitionAsync(BotConnectionState target, string? errorClass)
    {
        BotConnectionState previous;
        lock (_stateLock)
        {
            previous = _state;
            if (previous == target) return;
            _state = target;
        }
        var change = new BotConnectionStateChange(
            From: previous,
            To: target,
            ChangedAt: DateTimeOffset.UtcNow,
            ErrorClass: errorClass);
        var handler = StateChanged;
        if (handler is not null)
        {
            foreach (var sub in handler.GetInvocationList().Cast<Func<BotConnectionStateChange, Task>>())
            {
                try { await sub(change).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "FakeBotConnection StateChanged subscriber threw"); }
            }
        }
    }
}
