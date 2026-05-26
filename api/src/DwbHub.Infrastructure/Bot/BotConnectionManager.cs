using System.Collections.Concurrent;
using DwbHub.Application.Audit;
using DwbHub.Application.Bot;
using DwbHub.Application.Encryption;
using DwbHub.Application.Messaging;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Bot;

/// <summary>
/// Singleton-scoped IHostedService that manages per-guild Discord bot connections.
/// On startup, queries all active+credentialed guilds and connects them.
/// Exposes lifecycle hooks for controllers to invoke on credential / activation /
/// reconnect events. State is in-memory only — audit-log is the source of historical
/// truth; guilds.last_connected_at is the only DB persistence of connection state.
///
/// Note on tenant slug in audit payload: Guild has no Slug property (slug lives on
/// Tenant). The slug is resolved via ITenantRepository.GetByIdAsync inside
/// OnStateChangedAsync. If the tenant lookup fails the audit event is still written
/// with tenant_slug = null — this is preferable to losing the event entirely.
/// </summary>
public sealed class BotConnectionManager(
    IBotConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<BotConnectionManager> logger)
    : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<long, IBotConnection> _connections = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastManualReconnectAt = new();
    private bool _disposed;

    private const int ManualReconnectCoolDownSeconds = 60;

    /// <summary>
    /// Clock function used to determine the current time. Overridable in tests to advance time
    /// without real sleeps. Defaults to DateTimeOffset.UtcNow.
    /// </summary>
    internal Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Query all active guilds with credentials and open bot connections concurrently
    /// (up to 10 at a time). Called by the ASP.NET Core host on application start.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var guildRepo = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var candidates = await guildRepo.ListActiveWithCredentialsAsync(ct).ConfigureAwait(false);

        // Boot up to 10 guilds concurrently to avoid thundering herd on large tenants.
        using var sem = new SemaphoreSlim(10);
        await Task.WhenAll(candidates.Select(c => BootGuildAsync(c.GuildId, sem, ct))).ConfigureAwait(false);
    }

    /// <summary>
    /// Disconnect all active bot connections with a 5-second deadline, then dispose them.
    /// Called by the ASP.NET Core host on graceful shutdown.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var snapshot = _connections.Values.ToList();
        await Task.WhenAll(snapshot.Select(c =>
            SwallowAsync(() => c.DisconnectAsync(cts.Token)))).ConfigureAwait(false);
        await Task.WhenAll(snapshot.Select(c =>
            SwallowAsync(() => c.DisposeAsync().AsTask()))).ConfigureAwait(false);
        _connections.Clear();
    }

    /// <summary>
    /// Returns the current state of the per-guild bot connection, or <c>null</c> if
    /// the manager has no entry for this guildId (e.g., during the brief startup
    /// window before <see cref="StartAsync"/>'s initial sweep has booted the guild,
    /// or after the guild was deactivated/credentials removed).
    /// <c>null</c> is semantically distinct from <see cref="BotConnectionState.Disconnected"/>:
    /// the former means "no info", the latter means "actively disconnected".
    /// </summary>
    public BotConnectionState? GetState(long guildId)
        => _connections.TryGetValue(guildId, out var conn)
            ? conn.State
            : null;

    /// <summary>Called by BotCredentialsController on PUT — disconnect + reconnect with new token.</summary>
    public Task OnCredentialsChangedAsync(long guildId, CancellationToken ct)
        => WithLockAsync(guildId, () => ReconnectOneAsync(guildId, ct), ct);

    /// <summary>Called by BotCredentialsController on DELETE — disconnect without reconnect.</summary>
    public Task OnCredentialsRemovedAsync(long guildId, CancellationToken ct)
        => WithLockAsync(guildId, () => DisconnectAndRemoveAsync(guildId, ct), ct);

    /// <summary>Called by GuildsController POST /activate — connect if credentials present.</summary>
    public Task OnGuildActivatedAsync(long guildId, CancellationToken ct)
        => WithLockAsync(guildId, () => ReconnectOneAsync(guildId, ct), ct);

    /// <summary>Called by GuildsController POST /deactivate — disconnect unconditionally.</summary>
    public Task OnGuildDeactivatedAsync(long guildId, CancellationToken ct)
        => WithLockAsync(guildId, () => DisconnectAndRemoveAsync(guildId, ct), ct);

    /// <summary>Called by GuildsController POST /bot/reconnect — forced cycle regardless of current state.</summary>
    public async Task<ManualReconnectOutcome> OnManualReconnectAsync(long guildId, long actorUserId, CancellationToken ct)
    {
        // Acquire the per-guild semaphore directly so the cool-down check + stamp write
        // are atomic. This prevents two concurrent HTTP requests both passing the gate.
        var sem = _locks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _now();
            if (_lastManualReconnectAt.TryGetValue(guildId, out var last))
            {
                var elapsed = now - last;
                if (elapsed < TimeSpan.FromSeconds(ManualReconnectCoolDownSeconds))
                {
                    var remaining = (int)Math.Ceiling(
                        (TimeSpan.FromSeconds(ManualReconnectCoolDownSeconds) - elapsed).TotalSeconds);
                    return new ManualReconnectOutcome.CoolDownActive(Math.Max(1, remaining));
                }
            }
            _lastManualReconnectAt[guildId] = now;
        }
        finally
        {
            sem.Release();
        }

        // Fire-and-forget the reconnect so the HTTP response returns immediately.
        // WithLockAsync re-acquires the same semaphore, keeping the actual reconnect
        // serialized against credential changes, activation, etc.
        _ = Task.Run(async () =>
        {
            try
            {
                await WithLockAsync(guildId, () => ReconnectOneAsync(guildId, ct), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background manual reconnect failed for guild {GuildId}", guildId);
            }
        }, CancellationToken.None);

        return new ManualReconnectOutcome.Triggered();
    }

    // --- internals ---

    private async Task BootGuildAsync(long guildId, SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WithLockAsync(guildId, () => ReconnectOneAsync(guildId, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Boot-load connect failed for guild {GuildId}", guildId);
        }
        finally { sem.Release(); }
    }

    private async Task WithLockAsync(long guildId, Func<Task> action, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        bool shouldCleanupLock = false;
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
            // If the action removed the connection, the lock is no longer needed.
            shouldCleanupLock = !_connections.ContainsKey(guildId);
        }
        finally
        {
            sem.Release();
            if (shouldCleanupLock && _locks.TryRemove(guildId, out var removed))
                removed.Dispose();
        }
    }

    private async Task ReconnectOneAsync(long guildId, CancellationToken ct)
    {
        // 1. Disconnect existing connection if present.
        if (_connections.TryRemove(guildId, out var existing))
        {
            await SwallowAsync(() => existing.DisconnectAsync(ct)).ConfigureAwait(false);
            await SwallowAsync(() => existing.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        using var scope = scopeFactory.CreateScope();
        var guildRepo = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var credRepo = scope.ServiceProvider.GetRequiredService<IGuildBotCredentialRepository>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IBotTokenEncryptor>();

        // 2. Re-query — caller may have raced; only connect if still active+credentialed.
        var guild = await guildRepo.GetByIdAsync(guildId, ct).ConfigureAwait(false);
        if (guild is null || !guild.IsActive) return;

        var cred = await credRepo.GetByGuildIdAsync(guildId, guild.TenantId, ct).ConfigureAwait(false);
        if (cred is null) return;

        var envelope = new CipherEnvelope(cred.Nonce, cred.Ciphertext, cred.Tag);
        var plaintext = encryptor.Decrypt(envelope);

        var conn = connectionFactory.Create(guildId, guild.TenantId);
        // Subscribe BEFORE adding to map so the first StateChanged is observed.
        conn.StateChanged += change => OnStateChangedAsync(guild, change);
        // Attach message event handlers so every inbound gateway event is routed
        // to IMessageService before the first READY fires from ConnectAsync.
        AttachMessageEventHandlers(conn);
        _connections[guildId] = conn;

        try
        {
            await conn.ConnectAsync(plaintext, ct).ConfigureAwait(false);
        }
        catch
        {
            // ConnectAsync threw (vs. normal state-transition outcome). Remove the
            // half-initialised connection so a subsequent reconnect doesn't see stale state.
            if (_connections.TryRemove(guildId, out var bad))
            {
                await SwallowAsync(() => bad.DisposeAsync().AsTask()).ConfigureAwait(false);
            }
            throw;
        }
        // plaintext is now eligible for GC.
    }

    private async Task DisconnectAndRemoveAsync(long guildId, CancellationToken ct)
    {
        if (_connections.TryRemove(guildId, out var conn))
        {
            await SwallowAsync(() => conn.DisconnectAsync(ct)).ConfigureAwait(false);
            await SwallowAsync(() => conn.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        // Always clear the manual-reconnect cool-down. With fire-and-forget reconnects
        // there may be no active connection yet when credentials are removed, but the
        // stamp should still be reset so a future re-activation gets a fresh window.
        _lastManualReconnectAt.TryRemove(guildId, out _);
    }

    private async Task OnStateChangedAsync(Guild guild, BotConnectionStateChange change)
    {
        var eventType = change.To switch
        {
            BotConnectionState.Connected => "bot.connected",
            BotConnectionState.Disconnected when change.From == BotConnectionState.Connected => "bot.disconnected",
            BotConnectionState.TokenInvalid => "bot.token_invalid",
            BotConnectionState.Failed => "bot.connection_failed",
            _ => null,
        };
        if (eventType is null) return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

            string? tenantSlug = null;
            try
            {
                var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                var tenant = await tenantRepo.GetByIdAsync(guild.TenantId, CancellationToken.None)
                    .ConfigureAwait(false);
                tenantSlug = tenant?.Slug;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resolve tenant slug for guild {GuildId} in audit event", guild.Id);
            }

            var payload = new Dictionary<string, object?>
            {
                ["tenantSlug"] = tenantSlug,
                ["guildPublicId"] = guild.PublicId.ToString("D"),
                ["connectedAt"] = change.To == BotConnectionState.Connected ? change.ChangedAt : null,
                ["reason"] = change.To == BotConnectionState.Disconnected ? "graceful" : null,
                ["errorClass"] = change.ErrorClass,
            };

            await audit.RecordAsync(new AuditEvent(
                TenantId: guild.TenantId,
                ActorUserId: null,
                EventType: eventType,
                Payload: payload,
                IpAddress: null,
                UserAgent: null), CancellationToken.None).ConfigureAwait(false);

            if (change.To == BotConnectionState.Connected)
            {
                var guildRepo = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
                await guildRepo.UpdateLastConnectedAtAsync(guild.Id, change.ChangedAt, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to handle state-change for guild {GuildId} → {State}",
                guild.Id, change.To);
        }
    }

    /// <summary>
    /// Subscribes the three Discord message events on a newly-created connection to
    /// <see cref="IMessageService"/> via a per-event DI scope.
    ///
    /// Each handler creates a fresh scope so MessageService (scoped) gets its own DbConnection.
    ///
    /// Handlers swallow all exceptions: an unhandled exception inside a Discord.NET
    /// event handler would crash the gateway worker thread, silently dropping all
    /// subsequent events for that guild.
    ///
    /// Memory: the connection is disposed by <see cref="DisconnectAndRemoveAsync"/>
    /// when a guild is deactivated. The event-handler delegates are captured on the
    /// connection object and are collected with it — no explicit detach needed.
    /// </summary>
    private void AttachMessageEventHandlers(IBotConnection conn)
    {
        conn.MessageReceived += async evt =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IMessageService>();
                await svc.PersistInboundAsync(evt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PersistInboundAsync failed for guild {GuildId}", evt.GuildId);
            }
        };

        conn.MessageUpdated += async evt =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IMessageService>();
                await svc.PersistEditAsync(evt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PersistEditAsync failed for guild {GuildId}", evt.GuildId);
            }
        };

        conn.MessageDeleted += async evt =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IMessageService>();
                await svc.MarkDeletedAsync(evt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MarkDeletedAsync failed for guild {GuildId}", evt.GuildId);
            }
        };
    }

    private static async Task SwallowAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); } catch { /* defensive — never crash the manager */ }
    }

    /// <summary>Dispose all per-guild semaphores held by the lock map.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        foreach (var sem in _locks.Values) sem.Dispose();
        _locks.Clear();
        _disposed = true;
    }
}
