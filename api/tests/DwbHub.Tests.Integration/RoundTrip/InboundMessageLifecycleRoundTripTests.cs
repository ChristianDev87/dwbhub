using System.Net.Http.Json;
using System.Text.Json;
using Discord;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;

namespace DwbHub.Tests.Integration.RoundTrip;

/// <summary>
/// Phase 2.2: Inbound Discord round-trip test via SignalR hub.
///
/// Validates the inbound pipeline direction:
///   Helper Bot (Bot B) → Discord Gateway → App's Bot A → MessageService.PersistInboundAsync
///   → SignalRMessagesBroadcaster → MessagesHub → Test SignalR client
///
/// The test also verifies the DB persistence side by querying
/// GET /api/t/{slug}/channels/{id}/messages after the SignalR event arrives.
///
/// SKIPPED unless all four env vars are set:
///   DISCORD_DEV_BOT_TOKEN, DISCORD_DEV_GUILD_ID, DISCORD_DEV_CHANNEL_ID,
///   DISCORD_DEV_HELPER_BOT_TOKEN
///
/// The helper bot must have SEND_MESSAGES + READ_MESSAGE_HISTORY + MANAGE_MESSAGES
/// in the test channel. MANAGE_MESSAGES is required to sweep leftover marker messages.
///
/// Race prevention: the SignalR On&lt;T&gt; handler is registered BEFORE the helper bot
/// posts, so no event can be missed.
/// </summary>
public sealed class InboundMessageLifecycleRoundTripTests(
    PostgresFixture fixture, ITestOutputHelper output)
    : RoundTripTestBase(fixture, output)
{
    // ── Marker prefix — unique per run for safe cleanup ────────────────────────
    private static readonly string InboundMarkerPrefix =
        $"[dwbhub-roundtrip-inbound:{Guid.NewGuid():N}]";

    protected override string TenantSlug => "roundtrip-inbound";
    protected override string MarkerPrefix => "[dwbhub-roundtrip-inbound:";
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
        // 1. Stop SignalR client gracefully.
        if (_hubClient is not null)
        {
            try { await _hubClient.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }
            try { await _hubClient.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }

        // 2. Clean up helper bot messages — sweep by marker prefix.
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
                            m.Content.StartsWith("[dwbhub-roundtrip-inbound:", StringComparison.Ordinal)))
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

    // ── Test: inbound message from helper bot arrives via SignalR + DB ─────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Discord_message_via_helper_bot_arrives_via_signalr_and_in_db()
    {
        Skip.IfNot(EnvOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_HELPER_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        var marker = $"{InboundMarkerPrefix} hello-from-helper";

        // ── Register SignalR handler BEFORE the helper bot sends (race prevention) ──
        var receivedTcs = new TaskCompletionSource<InboundHubPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = _hubClient!.On<JsonElement>("MessageReceived", element =>
        {
            // Deserialize into the shape that SignalRMessagesBroadcaster sends:
            // MessageBroadcastDto { Id, TenantId, ChannelPublicId, AuthorName,
            //                       Content, SentAt, ViaDwbhub, DiscordMessageId }
            try
            {
                var payload = new InboundHubPayload(
                    Content: element.GetProperty("content").GetString() ?? "",
                    AuthorName: element.GetProperty("authorName").GetString() ?? "",
                    DiscordMessageId: element.GetProperty("discordMessageId").GetInt64(),
                    ViaDwbhub: element.GetProperty("viaDwbhub").GetBoolean());

                if (payload.Content == marker)
                    receivedTcs.TrySetResult(payload);
            }
            catch
            {
                // Payload shape mismatch — do not crash the hub pump;
                // the TCS will timeout and the assertion will fail with a clear message.
            }
        });

        // ── Helper bot posts the marker message ────────────────────────────────
        Log("Step: helper bot posting marker message");
        var channel = await HelperBot!.GetChannelAsync(TestChannelDiscordId)
            .ConfigureAwait(false) as IMessageChannel;
        channel.Should().NotBeNull(
            $"Helper bot must be able to see channel {TestChannelDiscordId}; " +
            "check that it is a member of the guild with READ_MESSAGES");

        var sentMessage = await channel!.SendMessageAsync(marker).ConfigureAwait(false);
        HelperPostedMessageIds.Add(sentMessage.Id);
        Log($"Step: helper bot posted, snowflake={sentMessage.Id}");

        // ── Wait for the SignalR event with a 20-second deadline ───────────────
        // Pipeline: Discord → Gateway → DiscordNetBotConnection.OnMessageReceivedAsync
        //           → MessageService.PersistInboundAsync → SignalRMessagesBroadcaster
        //           → MessagesHub → LongPolling client
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        InboundHubPayload received;
        try
        {
            received = await receivedTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Provide a clear failure message rather than a cryptic cancellation.
            throw new TimeoutException(
                $"SignalR MessageReceived event was not delivered within 20 seconds " +
                $"for marker '{marker}'. " +
                "Possible causes: Bot A gateway not connected, channel not bridged, " +
                "or webhook-loop suppression incorrectly fired.");
        }

        Log("Step: SignalR MessageReceived event arrived");

        // ── Assertions: SignalR payload ────────────────────────────────────────
        received.Content.Should().Be(marker,
            "the hub payload content must match the exact text posted by the helper bot");
        received.ViaDwbhub.Should().BeFalse(
            "an inbound gateway message is NOT sent via DwbHub");
        received.DiscordMessageId.Should().Be((long)sentMessage.Id,
            "the Discord snowflake in the hub payload must match the message actually posted");

        // ── DB verification: the message must be persisted ─────────────────────
        // Poll with a short window to absorb any lag between broadcast and commit.
        Log("Step: polling REST /messages for DB persistence");
        var dbMessage = await PollForDbMessageAsync(marker, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        dbMessage.Should().NotBeNull(
            $"GET /messages must return the inbound message within 10s of the SignalR event; " +
            $"marker='{marker}'");
        dbMessage!.Content.Should().Be(marker);
        dbMessage.DiscordMessageId.Should().Be((long)sentMessage.Id);
        Log("Step: DB persistence confirmed");
    }

    // ── Helper: poll REST /messages until the marker appears ──────────────────

    private async Task<HistoryItem?> PollForDbMessageAsync(string content, TimeSpan timeout)
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
                        .ReadFromJsonAsync<MessageHistoryApiResponse>()
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

/// <summary>
/// Projection of the fields we assert on from the SignalR MessageReceived payload.
/// The actual wire payload is <c>MessageBroadcastDto</c> from
/// <c>DwbHub.Application.Messaging</c>; camelCase JSON names are used by System.Text.Json.
/// </summary>
file sealed record InboundHubPayload(
    string Content,
    string AuthorName,
    long DiscordMessageId,
    bool ViaDwbhub);

/// <summary>Response shape from GET /api/t/{slug}/channels/{id}/messages.</summary>
internal sealed record MessageHistoryApiResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("messages")]
    List<HistoryItem>? Messages,
    [property: System.Text.Json.Serialization.JsonPropertyName("nextBefore")]
    long? NextBefore);

internal sealed record HistoryItem(
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
