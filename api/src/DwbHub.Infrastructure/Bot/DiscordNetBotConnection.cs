using Discord;
using Discord.WebSocket;
using DwbHub.Application.Bot;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Bot;

/// <summary>
/// Discord.NET-backed implementation of <see cref="IBotConnection"/>.
/// One instance per guild; owns its own <see cref="DiscordSocketClient"/>.
/// </summary>
public sealed class DiscordNetBotConnection : IBotConnection, IAsyncDisposable
{
    private readonly long _guildId;
    private readonly long _tenantId;
    private readonly ILogger<DiscordNetBotConnection> _logger;
    private readonly DiscordSocketClient _client;
    private readonly object _stateLock = new();
    private BotConnectionState _state = BotConnectionState.Disconnected;
    private DateTimeOffset? _lastConnectedAt;
    private volatile bool _disposed;

    public DiscordNetBotConnection(long guildId, long tenantId, ILogger<DiscordNetBotConnection> logger)
    {
        _guildId = guildId;
        _tenantId = tenantId;
        _logger = logger;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            LogLevel = LogSeverity.Warning,
            MessageCacheSize = 0,
            AlwaysDownloadUsers = false,
            ConnectionTimeout = 30_000,
        });
        _client.Ready        += OnReadyAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.LoggedOut    += OnLoggedOutAsync;
    }

    public long GuildId => _guildId;
    public long TenantId => _tenantId;
    public BotConnectionState State { get { lock (_stateLock) return _state; } }
    public DateTimeOffset? LastConnectedAt { get { lock (_stateLock) return _lastConnectedAt; } }

    public event Func<BotConnectionStateChange, Task>? StateChanged;

    public async Task ConnectAsync(string botToken, CancellationToken ct)
    {
        ThrowIfDisposed();
        TransitionTo(BotConnectionState.Connecting, errorClass: null);
        try
        {
            await _client.LoginAsync(TokenType.Bot, botToken);
            await _client.StartAsync();
            // State becomes Connected when the Ready event fires asynchronously.
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Defensive: Discord.NET 3.16 does not actually throw here for invalid tokens —
            // the primary invalid-token path is the async 4004 close handled in OnDisconnectedAsync.
            // Kept in case a future Discord.NET version resumes synchronous 401 behaviour.
            _logger.LogWarning("Discord rejected bot token (401 Unauthorized) for guild {GuildId} — token must be rotated", _guildId);
            TransitionTo(BotConnectionState.TokenInvalid, errorClass: "token_invalid");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bot connect failed for guild {GuildId}", _guildId);
            TransitionTo(BotConnectionState.Failed, errorClass: "connect_failed");
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (_disposed) return;
        await DisconnectCoreAsync();
    }

    private async Task DisconnectCoreAsync()
    {
        try
        {
            await _client.LogoutAsync();
            await _client.StopAsync();
        }
        finally
        {
            TransitionTo(BotConnectionState.Disconnected, errorClass: null);
        }
    }

    private Task OnReadyAsync()
    {
        lock (_stateLock) { _lastConnectedAt = DateTimeOffset.UtcNow; }
        TransitionTo(BotConnectionState.Connected, errorClass: null);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception ex)
    {
        // Discord.NET fires this on transient disconnects too; it will auto-reconnect.
        // We surface the state so the UI shows a yellow indicator until Ready fires again.
        _logger.LogInformation("Bot disconnected for guild {GuildId}: {Reason}", _guildId, ex?.Message ?? "unknown");

        // Discord gateway close code 4004 = AUTHENTICATION_FAILED. The token is invalid
        // (or shape-valid but revoked / not a real bot token). Mark as TokenInvalid so
        // the manager does NOT auto-reconnect (would burn rate limit).
        if (ex is Discord.Net.WebSocketClosedException ws && ws.CloseCode == 4004)
        {
            _logger.LogWarning("Discord gateway closed connection for guild {GuildId} with code 4004 (Authentication Failed) — token must be rotated", _guildId);
            TransitionTo(BotConnectionState.TokenInvalid, errorClass: "token_invalid");
            return Task.CompletedTask;
        }

        if (State == BotConnectionState.Connected)
        {
            TransitionTo(BotConnectionState.Connecting, errorClass: "transient_disconnect");
        }
        return Task.CompletedTask;
    }

    private Task OnLoggedOutAsync()
    {
        TransitionTo(BotConnectionState.Disconnected, errorClass: null);
        return Task.CompletedTask;
    }

    private void TransitionTo(BotConnectionState newState, string? errorClass)
    {
        BotConnectionState oldState;
        lock (_stateLock)
        {
            if (_state == newState) return;
            oldState = _state;
            _state = newState;
        }
        var change = new BotConnectionStateChange(oldState, newState, DateTimeOffset.UtcNow, errorClass);
        // Snapshot the handler list outside the lock — standard C# pattern to avoid
        // holding the lock while invoking subscribers. Concurrent unsubscribe is safe
        // because the captured delegate list is immutable; concurrent dispose is safe
        // because Task.Run wraps invocation with try/catch.
        var handler = StateChanged;
        if (handler is null) return;
        // Fire-and-forget the async handlers; manager catches its own exceptions.
        _ = Task.Run(async () =>
        {
            try { await handler(change); }
            catch (Exception ex) { _logger.LogError(ex, "StateChanged handler threw for guild {GuildId}", _guildId); }
        });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DiscordNetBotConnection));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe Discord.NET events explicitly so a late-fired event won't
        // attempt to TransitionTo on a disposed instance (also helps reviewers see
        // the symmetric subscribe/unsubscribe pattern).
        _client.Ready          -= OnReadyAsync;
        _client.Disconnected   -= OnDisconnectedAsync;
        _client.LoggedOut      -= OnLoggedOutAsync;

        try { await DisconnectCoreAsync(); }
        catch { /* best-effort during dispose */ }
        await _client.DisposeAsync();
    }
}
