using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Discord;
using DwbHub.Application.Bot;
using DwbHub.Infrastructure.Bot;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DwbHub.Tests.Integration.RoundTrip;

/// <summary>
/// Phase 2.3: Bot-reconnect round-trip test.
///
/// Validates that POST /api/t/{slug}/guilds/{id}/bot/reconnect triggers a real
/// gateway reconnect, after which the bot remains fully functional:
///   - outbound: gateway is connected
///   - inbound: Helper Bot (Bot B) posts a marker message, which App's Bot A
///     receives via the Gateway event, persists via MessageService.PersistInboundAsync,
///     broadcasts via SignalRMessagesBroadcaster, and the test client receives it
///     via the MessagesHub.
///   - persistence: the message is queryable via GET /messages within 10 s of the SignalR event.
///
/// SKIPPED unless all four env vars are set:
///   DISCORD_DEV_BOT_TOKEN, DISCORD_DEV_GUILD_ID, DISCORD_DEV_CHANNEL_ID,
///   DISCORD_DEV_HELPER_BOT_TOKEN
///
/// Race prevention: the SignalR On&lt;T&gt; handler is registered BEFORE the helper bot
/// posts, so no event can be missed.
///
/// Cool-down bypass: BotConnectionManager._now is set to a future instant before
/// calling the reconnect endpoint, preventing the 60-second manual-reconnect
/// cool-down from throttling the first reconnect request in the test.
/// </summary>
public sealed class BotReconnectRoundTripTests(
    PostgresFixture fixture, ITestOutputHelper output)
    : RoundTripTestBase(fixture, output)
{
    // ── Marker prefix — unique per run for safe cleanup ────────────────────────
    private static readonly string ReconnectMarkerPrefix =
        $"[dwbhub-roundtrip-reconnect:{Guid.NewGuid():N}]";

    protected override string TenantSlug => "roundtrip-reconnect";
    protected override string MarkerPrefix => "[dwbhub-roundtrip-reconnect:";
    protected override bool RequiresHelperBot => true;

    // ── SignalR client ─────────────────────────────────────────────────────────
    private HubConnection? _hubClient;

    // ── Sub-class setup hook ───────────────────────────────────────────────────

    protected override async Task OnSetupCompleted()
    {
        // Build the SignalR client using the in-process test server handler.
        // LongPolling: same as existing hub tests — avoids Kestrel WebSocket plumbing.
        _hubClient = new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, "api/hubs/messages"),
                opts =>
                {
                    opts.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                    opts.Transports = HttpTransportType.LongPolling;
                    opts.AccessTokenProvider = () => Task.FromResult<string?>(OwnerJwt);
                })
            .Build();
        await _hubClient.StartAsync().ConfigureAwait(false);
        // The hub's OnConnectedAsync adds us to the tenant group automatically via
        // the JWT "tid" claim — no explicit subscribe call required.
        Log("Setup: SignalR client connected");
    }

    // ── Sub-class teardown hook ────────────────────────────────────────────────

    protected override async Task OnTeardown()
    {
        // 1. Restore real clock in the manager (defensive — isolated per factory, but clean is clean).
        if (Factory is not null)
        {
            try
            {
                var manager = Factory.Services.GetRequiredService<BotConnectionManager>();
                manager._now = () => DateTimeOffset.UtcNow;
            }
            catch { /* swallow */ }
        }

        // 2. Stop SignalR client gracefully.
        if (_hubClient is not null)
        {
            try { await _hubClient.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }
            try { await _hubClient.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }

        // 3. Clean up helper bot messages — sweep by marker prefix.
        if (HelperBot is not null)
        {
            try
            {
                var channel = await HelperBot
                    .GetChannelAsync(TestChannelDiscordId)
                    .ConfigureAwait(false) as IMessageChannel;

                if (channel is not null)
                {
                    try
                    {
                        var msgs = await channel.GetMessagesAsync(50)
                            .FlattenAsync().ConfigureAwait(false);
                        int swept = 0;
                        foreach (var msg in msgs.Where(m =>
                            m.Content.StartsWith("[dwbhub-roundtrip-reconnect:", StringComparison.Ordinal)))
                        {
                            try { await msg.DeleteAsync().ConfigureAwait(false); swept++; } catch { /* swallow */ }
                        }
                        Log($"Teardown: helper bot message sweep ({swept} message(s))");
                    }
                    catch { /* swallow */ }
                }
            }
            catch { /* swallow */ }
        }
    }

    // ── Test: post-reconnect bot receives inbound messages + SignalR + DB ─────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Reconnect_via_api_keeps_bot_functional()
    {
        Skip.IfNot(EnvOk, "Discord round-trip env (incl. helper token) not set");

        var manager = Factory.Services.GetRequiredService<BotConnectionManager>();

        // 1) Cool-down bypass: advance the manager's clock by 10 minutes so that if
        //    _lastManualReconnectAt already has an entry (e.g., from a previous test
        //    in the same process), the cool-down check passes immediately.
        //    After the reconnect completes we restore real-time so WaitForBotConnectedAsync
        //    works against actual state transitions.
        var frozenNow = DateTimeOffset.UtcNow.AddMinutes(10);
        manager._now = () => frozenNow;

        // 2) Trigger reconnect via App-API.
        Log("Step: triggering reconnect via API");
        var reconnectRes = await Client.ReconnectBotAsync(TenantSlug, GuildPublicId);
        reconnectRes.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "reconnect endpoint must return 204 — a 429 means the cool-down override did not take effect");
        Log("Step: reconnect returned 204");

        // Restore real time now that the cooldown gate is past.
        manager._now = () => DateTimeOffset.UtcNow;

        // 3) Wait until BotConnectionState returns to Connected.
        //    OnManualReconnectAsync is fire-and-forget — the HTTP response arrives before
        //    the reconnect is fully established, so we must poll explicitly.
        Log("Step: waiting for bot to reconnect");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await WaitForBotConnectedAsync(manager, GuildId, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        sw.Stop();
        Log($"Step: bot reconnected ({sw.Elapsed.TotalSeconds:F1}s)");

        // 4) Subscribe on SignalR MessageReceived BEFORE the helper bot posts (race prevention).
        var marker = $"{ReconnectMarkerPrefix} post-reconnect-ping";
        var receivedTcs = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = _hubClient!.On<JsonElement>("MessageReceived", element =>
        {
            try
            {
                var content = element.GetProperty("content").GetString();
                if (content == marker)
                    receivedTcs.TrySetResult(element);
            }
            catch
            {
                // Payload shape mismatch — do not crash the hub pump;
                // the TCS will timeout and the assertion will fail with a clear message.
            }
        });

        // 5) Helper bot posts — this is the functional proof that the gateway is alive.
        Log("Step: helper bot posting marker message");
        var channel = await HelperBot!.GetChannelAsync(TestChannelDiscordId)
            .ConfigureAwait(false) as IMessageChannel;
        channel.Should().NotBeNull(
            $"Helper bot must be able to see channel {TestChannelDiscordId}; " +
            "check that it is a member of the guild with SEND_MESSAGES");

        var sent = await channel!.SendMessageAsync(marker).ConfigureAwait(false);
        HelperPostedMessageIds.Add(sent.Id);
        Log($"Step: helper bot posted, snowflake={sent.Id}");

        // 6) Within 20 s the SignalR event must arrive — gateway is post-reconnect functional.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        JsonElement hubPayload;
        try
        {
            hubPayload = await receivedTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"SignalR MessageReceived was not delivered within 20 s for marker '{marker}'. " +
                "Possible causes: Bot A gateway not re-established after reconnect, " +
                "channel not bridged, or webhook-loop suppression incorrectly fired.");
        }

        Log("Step: SignalR MessageReceived event arrived");

        hubPayload.GetProperty("content").GetString().Should().Be(marker,
            "the hub payload content must match the exact text posted by the helper bot");
        hubPayload.GetProperty("viaDwbhub").GetBoolean().Should().BeFalse(
            "an inbound gateway message is NOT sent via DwbHub");
        hubPayload.GetProperty("discordMessageId").GetInt64().Should().Be((long)sent.Id,
            "the Discord snowflake in the hub payload must match the message actually posted");

        // 7) DB persistence path must still work after a reconnect.
        //    Poll with a short window to absorb any lag between broadcast and commit.
        Log("Step: polling REST /messages for DB persistence");
        var dbMessage = await PollForDbMessageAsync(marker, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        dbMessage.Should().NotBeNull(
            $"GET /messages must return the inbound message within 10 s of the SignalR event; " +
            $"marker='{marker}'");
        dbMessage!.Content.Should().Be(marker);
        dbMessage.DiscordMessageId.Should().Be((long)sent.Id);
        Log("Step: DB persistence confirmed");
    }

    // ── Helper: poll REST /messages until the marker appears ──────────────────

    private async Task<ReconnectHistoryItem?> PollForDbMessageAsync(string content, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var resp = await Client.GetMessagesAsync(TenantSlug, BridgedChannelPublicId, limit: 20)
                    .ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content
                        .ReadFromJsonAsync<ReconnectMessageHistoryApiResponse>()
                        .ConfigureAwait(false);
                    var match = body?.Messages?.FirstOrDefault(m => m.Content == content);
                    if (match is not null) return match;
                }
            }
            catch
            {
                // Transient — keep polling.
            }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        return null;
    }
}

// ── Local types ───────────────────────────────────────────────────────────────

/// <summary>Response shape from GET /api/t/{slug}/channels/{id}/messages.</summary>
internal sealed record ReconnectMessageHistoryApiResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("messages")]
    List<ReconnectHistoryItem>? Messages,
    [property: System.Text.Json.Serialization.JsonPropertyName("nextBefore")]
    long? NextBefore);

internal sealed record ReconnectHistoryItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")]
    long Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("publicId")]
    Guid PublicId,
    [property: System.Text.Json.Serialization.JsonPropertyName("authorName")]
    string AuthorName,
    [property: System.Text.Json.Serialization.JsonPropertyName("content")]
    string Content,
    [property: System.Text.Json.Serialization.JsonPropertyName("sentAt")]
    DateTimeOffset SentAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("viaDwbhub")]
    bool ViaDwbhub,
    [property: System.Text.Json.Serialization.JsonPropertyName("discordMessageId")]
    long DiscordMessageId);
