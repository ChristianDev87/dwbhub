using Discord;
using Discord.WebSocket;
using DwbHub.Application.Bot;
using DwbHub.Application.Messaging;
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
            GatewayIntents = GatewayIntents.Guilds
                          | GatewayIntents.GuildMessages
                          | GatewayIntents.MessageContent      // privileged — see docs/SETUP-MESSAGE-CONTENT-INTENT.md
                          | GatewayIntents.GuildWebhooks,
            LogLevel = LogSeverity.Warning,
            MessageCacheSize = 0,
            AlwaysDownloadUsers = false,
            ConnectionTimeout = 30_000,
        });
        _client.Ready += OnReadyAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.LoggedOut += OnLoggedOutAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.MessageUpdated += OnMessageUpdatedAsync;
        _client.MessageDeleted += OnMessageDeletedAsync;
    }

    /// <inheritdoc/>
    public long GuildId => _guildId;
    /// <inheritdoc/>
    public long TenantId => _tenantId;
    /// <inheritdoc/>
    public BotConnectionState State { get { lock (_stateLock) return _state; } }
    /// <inheritdoc/>
    public DateTimeOffset? LastConnectedAt { get { lock (_stateLock) return _lastConnectedAt; } }

    /// <inheritdoc/>
    public event Func<BotConnectionStateChange, Task>? StateChanged;
    /// <inheritdoc/>
    public event Func<MessageReceivedEvent, Task>? MessageReceived;
    /// <inheritdoc/>
    public event Func<MessageUpdatedEvent, Task>? MessageUpdated;
    /// <inheritdoc/>
    public event Func<MessageDeletedEvent, Task>? MessageDeleted;

    /// <inheritdoc/>
    /// <remarks>
    /// Starts the Discord.NET gateway login and socket. State becomes
    /// <see cref="BotConnectionState.Connected"/> asynchronously when the
    /// Discord READY event fires, not when this method returns.
    /// Invalid tokens surface via the async 4004 close code handled in
    /// <c>OnDisconnectedAsync</c> rather than as a synchronous 401.
    /// </remarks>
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
            // Synchronous 401 path: REST identify-call rejected the token.
            // OnDisconnectedAsync covers the async path (4004 gateway close + late-arriving 401s).
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

    /// <inheritdoc/>
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

    /// <summary>
    /// Classify a disconnect exception as a Discord credential rejection (token revoked / wrong).
    /// Two paths cover the same root cause: the gateway closes with code 4004
    /// (AUTHENTICATION_FAILED), or the REST identify-call returns 401 Unauthorized.
    /// Either way the client should stop — Discord.NET would otherwise keep retrying
    /// on its internal schedule and burn rate limit until the API bans the token.
    /// </summary>
    public static bool IsAuthFailure(Exception? ex)
    {
        if (ex is Discord.Net.WebSocketClosedException ws && ws.CloseCode == 4004)
            return true;
        if (ex is Discord.Net.HttpException http && http.HttpCode == System.Net.HttpStatusCode.Unauthorized)
            return true;
        return false;
    }

    private Task OnDisconnectedAsync(Exception ex)
    {
        // Discord.NET fires this on transient disconnects too; it will auto-reconnect.
        // We surface the state so the UI shows a yellow indicator until Ready fires again.
        _logger.LogInformation("Bot disconnected for guild {GuildId}: {Reason}", _guildId, ex?.Message ?? "unknown");

        if (IsAuthFailure(ex))
        {
            _logger.LogWarning("Discord rejected bot credentials for guild {GuildId} ({Reason}) — token must be rotated. Stopping client to prevent reconnect loop.", _guildId, ex?.GetType().Name ?? "unknown");
            TransitionTo(BotConnectionState.TokenInvalid, errorClass: "token_invalid");

            // Stop Discord.NET's internal reconnect loop. Run on a background task —
            // calling StopAsync inside the Disconnected event handler can deadlock
            // with Discord.NET's own dispatch lock.
            _ = Task.Run(async () =>
            {
                try { await _client.StopAsync(); }
                catch (Exception stopEx)
                {
                    _logger.LogDebug(stopEx, "StopAsync after auth-failure disconnect failed for guild {GuildId}", _guildId);
                }
            });

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

    private Task OnMessageReceivedAsync(SocketMessage msg)
    {
        // Skip DMs — only handle guild channel messages.
        if (msg.Channel is not SocketGuildChannel guildChannel)
            return Task.CompletedTask;

        // Skip self-echo — messages we sent ourselves.
        if (msg.Author.Id == _client.CurrentUser?.Id)
            return Task.CompletedTask;

        var evt = new MessageReceivedEvent
        {
            TenantId = _tenantId,
            GuildId = _guildId,
            DiscordChannelId = (long)guildChannel.Id,
            DiscordMessageId = (long)msg.Id,
            DiscordAuthorId = (long)msg.Author.Id,
            DiscordAuthorName = msg.Author.Username,
            AuthorIsWebhook = msg.Author.IsWebhook,
            WebhookSourceId = msg.Author.IsWebhook ? msg.Author.Id : null,
            Content = msg.Content ?? "",
            SentAt = msg.Timestamp,           // Discord-supplied; NEVER UtcNow
        };

        var handler = MessageReceived;
        if (handler is null) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await handler(evt); }
            catch (Exception ex) { _logger.LogError(ex, "MessageReceived handler threw for guild {GuildId}", _guildId); }
        });
        return Task.CompletedTask;
    }

    private Task OnMessageUpdatedAsync(
        Cacheable<IMessage, ulong> _before,
        SocketMessage updated,
        ISocketMessageChannel channel)
    {
        // Discord fires this event for embed-resolution even when the user didn't
        // edit the text. Skip if there is no actual EditedTimestamp.
        if (updated.EditedTimestamp is null)
            return Task.CompletedTask;

        if (channel is not SocketGuildChannel guildChannel)
            return Task.CompletedTask;

        var evt = new MessageUpdatedEvent
        {
            TenantId = _tenantId,
            GuildId = _guildId,
            DiscordChannelId = (long)guildChannel.Id,
            DiscordMessageId = (long)updated.Id,
            Content = updated.Content ?? "",
            EditedAt = updated.EditedTimestamp.Value,   // Discord-supplied; NEVER UtcNow
        };

        var handler = MessageUpdated;
        if (handler is null) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await handler(evt); }
            catch (Exception ex) { _logger.LogError(ex, "MessageUpdated handler threw for guild {GuildId}", _guildId); }
        });
        return Task.CompletedTask;
    }

    private Task OnMessageDeletedAsync(
        Cacheable<IMessage, ulong> cached,
        Cacheable<IMessageChannel, ulong> channelCacheable)
    {
        // Resolve the channel from the cacheable; if it's not a guild channel, skip.
        if (channelCacheable.HasValue && channelCacheable.Value is not SocketGuildChannel)
            return Task.CompletedTask;

        // We may not have the channel value if it wasn't cached — use the raw ID and
        // let the consumer handle unknown-channel gracefully.
        var evt = new MessageDeletedEvent
        {
            TenantId = _tenantId,
            GuildId = _guildId,
            DiscordChannelId = (long)channelCacheable.Id,
            DiscordMessageId = (long)cached.Id,
        };

        var handler = MessageDeleted;
        if (handler is null) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await handler(evt); }
            catch (Exception ex) { _logger.LogError(ex, "MessageDeleted handler threw for guild {GuildId}", _guildId); }
        });
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

    /// <summary>
    /// Unsubscribe all Discord.NET event handlers, disconnect gracefully, and dispose
    /// the underlying <see cref="Discord.WebSocket.DiscordSocketClient"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe Discord.NET events explicitly so a late-fired event won't
        // attempt to TransitionTo on a disposed instance (also helps reviewers see
        // the symmetric subscribe/unsubscribe pattern).
        _client.Ready -= OnReadyAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.LoggedOut -= OnLoggedOutAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.MessageUpdated -= OnMessageUpdatedAsync;
        _client.MessageDeleted -= OnMessageDeletedAsync;

        try { await DisconnectCoreAsync(); }
        catch { /* best-effort during dispose */ }
        await _client.DisposeAsync();
    }
}
