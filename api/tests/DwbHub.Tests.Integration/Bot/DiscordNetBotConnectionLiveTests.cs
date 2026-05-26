using DwbHub.Application.Bot;
using DwbHub.Infrastructure.Bot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DwbHub.Tests.Integration.Bot;

/// <summary>
/// Live Discord gateway tests. Skipped when DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID
/// are not set. CI runs them only on push to develop/main via a dedicated job with the
/// Discord secrets in scope. NEVER hard-code a token here.
///
/// NOTE on the absent TokenInvalid test: triggering Discord's gateway close code 4004
/// (AUTHENTICATION_FAILED) requires a syntactically well-formed but cryptographically
/// invalid token (e.g., a revoked real bot token). Synthetic fake tokens are rejected
/// by Discord.NET 3.x BEFORE the gateway handshake, never triggering 4004. The
/// production code path that handles 4004 (DiscordNetBotConnection.OnDisconnectedAsync)
/// is verified by code review against the Discord gateway spec rather than by a live
/// test. If a future PR introduces a revoked-token e2e setup, add the test back.
/// </summary>
[Trait("Category", "DiscordLive")]
public sealed class DiscordNetBotConnectionLiveTests
{
    private static (string token, long guildId)? ReadEnv()
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_DEV_BOT_TOKEN");
        var guildIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_GUILD_ID");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(guildIdStr)) return null;
        if (!long.TryParse(guildIdStr, out var guildId)) return null;
        return (token, guildId);
    }

    [SkippableFact]
    public async Task ConnectAsync_WithValidToken_TransitionsToConnected()
    {
        var env = ReadEnv();
        Skip.If(env is null, "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID not set");

        await using var conn = new DiscordNetBotConnection(
            guildId: env!.Value.guildId,
            tenantId: 1L,
            logger: NullLogger<DiscordNetBotConnection>.Instance,
            scopeFactory: NoOpScopeFactory.Instance);

        var transitions = new List<BotConnectionStateChange>();
        var reachedConnected = new TaskCompletionSource();
        conn.StateChanged += change =>
        {
            transitions.Add(change);
            if (change.To == BotConnectionState.Connected) reachedConnected.TrySetResult();
            return Task.CompletedTask;
        };

        await conn.ConnectAsync(env.Value.token, CancellationToken.None);

        // Discord gateway READY typically arrives in 2-5 s; allow 30 s for slow CI runners.
        var completed = await Task.WhenAny(reachedConnected.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(completed == reachedConnected.Task, "Did not reach Connected within 30 s");

        Assert.Equal(BotConnectionState.Connected, conn.State);
        Assert.NotNull(conn.LastConnectedAt);
        // Expect at least Disconnected->Connecting and Connecting->Connected.
        Assert.Contains(transitions, t => t.To == BotConnectionState.Connecting);
        Assert.Contains(transitions, t => t.To == BotConnectionState.Connected);
    }

    [SkippableFact]
    public async Task DisconnectAsync_AfterConnect_TransitionsToDisconnected()
    {
        var env = ReadEnv();
        Skip.If(env is null, "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID not set");

        await using var conn = new DiscordNetBotConnection(
            guildId: env!.Value.guildId,
            tenantId: 1L,
            logger: NullLogger<DiscordNetBotConnection>.Instance,
            scopeFactory: NoOpScopeFactory.Instance);

        var reachedConnected = new TaskCompletionSource();
        conn.StateChanged += change =>
        {
            if (change.To == BotConnectionState.Connected) reachedConnected.TrySetResult();
            return Task.CompletedTask;
        };

        await conn.ConnectAsync(env.Value.token, CancellationToken.None);
        var completed = await Task.WhenAny(reachedConnected.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(completed == reachedConnected.Task, "Did not reach Connected within 30 s");
        Assert.Equal(BotConnectionState.Connected, conn.State);

        await conn.DisconnectAsync(CancellationToken.None);
        Assert.Equal(BotConnectionState.Disconnected, conn.State);
    }
}

/// <summary>
/// No-op IServiceScopeFactory for live tests that don't need the bot permission check
/// to actually persist anything. The permission check is non-fatal; a thrown exception
/// is swallowed by the fire-and-forget wrapper in OnReadyAsync.
/// </summary>
file sealed class NoOpScopeFactory : IServiceScopeFactory
{
    public static readonly NoOpScopeFactory Instance = new();

    public IServiceScope CreateScope() => new NoOpScope();

    private sealed class NoOpScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new NoOpServiceProvider();
        public void Dispose() { }
    }

    private sealed class NoOpServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
