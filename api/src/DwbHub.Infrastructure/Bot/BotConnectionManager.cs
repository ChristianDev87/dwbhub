using System.Collections.Concurrent;
using DwbHub.Application.Audit;
using DwbHub.Application.Bot;
using DwbHub.Application.Encryption;
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
    private bool _disposed;

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var guildRepo = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var candidates = await guildRepo.ListActiveWithCredentialsAsync(ct).ConfigureAwait(false);

        // Boot up to 10 guilds concurrently to avoid thundering herd on large tenants.
        using var sem = new SemaphoreSlim(10);
        await Task.WhenAll(candidates.Select(c => BootGuildAsync(c.GuildId, sem, ct))).ConfigureAwait(false);
    }

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
    /// Returns the current in-memory state of a guild's bot connection.
    /// Returns Disconnected for unknown guildIds.
    /// </summary>
    public BotConnectionState GetState(long guildId)
        => _connections.TryGetValue(guildId, out var conn)
            ? conn.State
            : BotConnectionState.Disconnected;

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
    public Task OnManualReconnectAsync(long guildId, long actorUserId, CancellationToken ct)
        => WithLockAsync(guildId, () => ReconnectOneAsync(guildId, ct), ct);

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
        var credRepo  = scope.ServiceProvider.GetRequiredService<IGuildBotCredentialRepository>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IBotTokenEncryptor>();

        // 2. Re-query — caller may have raced; only connect if still active+credentialed.
        var guild = await guildRepo.GetByIdAsync(guildId, ct).ConfigureAwait(false);
        if (guild is null || !guild.IsActive) return;

        var cred = await credRepo.GetByGuildIdAsync(guildId, guild.TenantId, ct).ConfigureAwait(false);
        if (cred is null) return;

        // 3. Decrypt + build new connection.
        var envelope = new CipherEnvelope(cred.Nonce, cred.Ciphertext, cred.Tag);
        var plaintext = encryptor.Decrypt(envelope);

        var conn = connectionFactory.Create(guildId, guild.TenantId);
        // Subscribe BEFORE adding to map so the first StateChanged is observed.
        conn.StateChanged += change => OnStateChangedAsync(guild, change);
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
        if (!_connections.TryRemove(guildId, out var conn)) return;
        await SwallowAsync(() => conn.DisconnectAsync(ct)).ConfigureAwait(false);
        await SwallowAsync(() => conn.DisposeAsync().AsTask()).ConfigureAwait(false);
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

            // Guild has no Slug property — slug lives on Tenant.
            // Resolve via ITenantRepository; if lookup fails we write null rather than lose the event.
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
                ["tenant_slug"]     = tenantSlug,
                ["guild_public_id"] = guild.PublicId.ToString("D"),
                ["connected_at"]    = change.To == BotConnectionState.Connected ? change.ChangedAt : null,
                ["reason"]          = change.To == BotConnectionState.Disconnected ? "graceful" : null,
                ["error_class"]     = change.ErrorClass,
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

    private static async Task SwallowAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); } catch { /* defensive — never crash the manager */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var sem in _locks.Values) sem.Dispose();
        _locks.Clear();
        _disposed = true;
    }
}
