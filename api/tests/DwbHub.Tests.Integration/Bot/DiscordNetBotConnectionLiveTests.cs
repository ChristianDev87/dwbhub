using DwbHub.Application.Bot;
using DwbHub.Infrastructure.Bot;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DwbHub.Tests.Integration.Bot;

/// <summary>
/// Live Discord gateway tests. Skipped when DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID
/// are not set. CI runs them only on push to develop/main via a dedicated job with the
/// Discord secrets in scope. NEVER hard-code a token here.
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
            logger: NullLogger<DiscordNetBotConnection>.Instance);

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
    public async Task ConnectAsync_WithInvalidToken_TransitionsToTokenInvalid()
    {
        var env = ReadEnv();
        Skip.If(env is null, "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID not set");

        await using var conn = new DiscordNetBotConnection(
            guildId: env!.Value.guildId,
            tenantId: 1L,
            logger: NullLogger<DiscordNetBotConnection>.Instance);

        var reachedTokenInvalid = new TaskCompletionSource();
        conn.StateChanged += change =>
        {
            if (change.To == BotConnectionState.TokenInvalid) reachedTokenInvalid.TrySetResult();
            return Task.CompletedTask;
        };

        // Shape-valid but bogus token. Discord accepts the WebSocket and then closes
        // with code 4004 (Authentication Failed) — handled asynchronously by
        // DiscordNetBotConnection.OnDisconnectedAsync.
        const string fakeToken = "InvalidTokenShapeSegment000000000000000.NotReal.InvalidTokenFinalSegment0000000000000000";

        // ConnectAsync does NOT throw — Discord.NET's LoginAsync/StartAsync only
        // initiate the connection. The 4004 close arrives via the Disconnected event.
        await conn.ConnectAsync(fakeToken, CancellationToken.None);

        // Wait up to 30 s for the asynchronous 4004 close → TokenInvalid transition.
        var completed = await Task.WhenAny(reachedTokenInvalid.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(completed == reachedTokenInvalid.Task, "Did not reach TokenInvalid within 30 s");

        Assert.Equal(BotConnectionState.TokenInvalid, conn.State);
    }

    [SkippableFact]
    public async Task DisconnectAsync_AfterConnect_TransitionsToDisconnected()
    {
        var env = ReadEnv();
        Skip.If(env is null, "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID not set");

        await using var conn = new DiscordNetBotConnection(
            guildId: env!.Value.guildId,
            tenantId: 1L,
            logger: NullLogger<DiscordNetBotConnection>.Instance);

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
