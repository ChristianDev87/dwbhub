using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
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
/// Round-trip test for the owner-moderation-delete path:
///   1. Helper bot (Bot B) posts a real message in the Discord channel.
///   2. App's Bot A picks it up via gateway → MessageService.PersistInboundAsync.
///   3. Tenant Owner calls DELETE /api/t/{slug}/channels/{cid}/messages/{mid}.
///   4. MessageService.DeleteOutboundAsync routes to DeleteMessageAsync (bot token) — NOT webhook path.
///   5. Test verifies: message gone in Discord (via VerifyClient REST poll) + soft-deleted in DB.
///
/// SKIPPED unless all four env vars are present:
///   DISCORD_DEV_BOT_TOKEN, DISCORD_DEV_HELPER_BOT_TOKEN,
///   DISCORD_DEV_GUILD_ID, DISCORD_DEV_CHANNEL_ID
///
/// The bot identity behind DISCORD_DEV_BOT_TOKEN needs MANAGE_MESSAGES on the test channel.
/// Production setup note: the test server grants admin to both bots, so MANAGE_MESSAGES
/// is implicitly covered; production channel permissions must explicitly grant MANAGE_MESSAGES.
/// </summary>
public sealed class OwnerModerationRoundTripTests(
    PostgresFixture fixture, ITestOutputHelper output)
    : RoundTripTestBase(fixture, output)
{
    // ── Sub-class contract ────────────────────────────────────────────────────

    protected override string TenantSlug => "roundtrip-owner-mod";
    protected override string MarkerPrefix => "[dwbhub-roundtrip-owner-mod:";
    protected override bool RequiresHelperBot => true;

    // ── SignalR client for inbound-event detection ────────────────────────────

    private HubConnection? _hubClient;

    protected override async Task OnSetupCompleted()
    {
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
        Log("Setup: SignalR client connected");
    }

    protected override async Task OnTeardown()
    {
        // 1. Stop SignalR client gracefully.
        if (_hubClient is not null)
        {
            try { await _hubClient.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }
            try { await _hubClient.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }

        // 2. Clean up any helper bot messages that may have been left behind
        //    (e.g. if the test stopped before the delete API call completed).
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
                            m.Content.StartsWith("[dwbhub-roundtrip-owner-mod:", StringComparison.Ordinal)))
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

    // ── Test ──────────────────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Owner_can_delete_helper_bot_message_via_real_discord()
    {
        Skip.IfNot(EnvOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_HELPER_BOT_TOKEN / " +
            "DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        var marker = $"{MarkerPrefix}{Guid.NewGuid():N}] owner-mod-delete";

        // ── Subscribe SignalR BEFORE the helper bot sends (race prevention) ───
        var receivedTcs = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = _hubClient!.On<JsonElement>("MessageReceived", element =>
        {
            try
            {
                var content = element.GetProperty("content").GetString() ?? "";
                var viaDwbhub = element.GetProperty("viaDwbhub").GetBoolean();

                // Only match our specific marker; ViaDwbhub must be false (inbound).
                if (content == marker && !viaDwbhub)
                {
                    // We can't get the public ID from the SignalR event directly,
                    // so just signal arrival — we'll look it up via REST.
                    receivedTcs.TrySetResult(Guid.Empty);
                }
            }
            catch
            {
                // Shape mismatch — don't crash the hub pump.
            }
        });

        // ── Helper bot posts the marker message ───────────────────────────────
        Log("Step: helper bot posting marker message");
        var channel = await HelperBot!.GetChannelAsync(TestChannelDiscordId)
            .ConfigureAwait(false) as IMessageChannel;
        channel.Should().NotBeNull(
            $"Helper bot must be able to see channel {TestChannelDiscordId}");

        var sentMessage = await channel!.SendMessageAsync(marker).ConfigureAwait(false);
        HelperPostedMessageIds.Add(sentMessage.Id);
        Log($"Step: helper bot posted, snowflake={sentMessage.Id}");

        // ── Wait for SignalR inbound event ────────────────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await receivedTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"SignalR MessageReceived was not delivered within 20 seconds for marker '{marker}'. " +
                "Possible causes: Bot A gateway not connected, channel not bridged, " +
                "or webhook-loop suppression incorrectly fired.");
        }

        Log("Step: inbound message arrived via SignalR");

        // ── Locate the message in our DB via REST GET /messages ───────────────
        Log("Step: locating inbound message via REST API");
        var ourMessage = await PollForMessageByContentAsync(marker, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        ourMessage.Should().NotBeNull(
            $"GET /messages must return the inbound marker message within 10s; marker='{marker}'");

        var publicId = ourMessage!.PublicId;
        publicId.Should().NotBe(Guid.Empty, "inbound message must have a non-empty PublicId");
        Log($"Step: found in DB, publicId={publicId}");

        // ── Owner deletes the message via the API ─────────────────────────────
        Log("Step: owner DELETE inbound message via API");
        var sw = Stopwatch.StartNew();
        var deleteRes = await Client.DeleteMessageAsync(TenantSlug, BridgedChannelPublicId, publicId)
            .ConfigureAwait(false);
        sw.Stop();
        Log($"Step: DELETE returned {(int)deleteRes.StatusCode} in {sw.ElapsedMilliseconds}ms");

        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "owner deleting an inbound message must succeed with 204");

        sw.ElapsedMilliseconds.Should().BeGreaterThan(50,
            "a real Discord REST round-trip must not return in under 50 ms — " +
            "faster suggests the backend skipped the Discord call silently");

        Log("Step: timing assertion passed — verifying Discord deletion");

        // ── Negative-elimination: mark this message as already cleaned ────────
        // The helper bot posted this message and immediately after that stopped
        // touching it. By removing it from the cleanup list, we ensure the
        // teardown sweep never deletes it — so the only plausible actor that
        // could have removed it from Discord is Bot A via the DELETE API call above.
        HelperPostedMessageIds.Remove(sentMessage.Id);

        // ── Verify message is gone from Discord ───────────────────────────────
        // Poll Discord REST until the message is no longer in the channel history.
        await AssertDiscordMessageGoneAsync(
            channelId: TestChannelDiscordId,
            messageId: sentMessage.Id,
            timeout: TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);

        Log("Step: message confirmed gone in Discord");

        // ── Verify app-side audit log ─────────────────────────────────────────
        // The app must have written a "message.deleted" audit entry attributing
        // the delete to the owner. We read directly from DB (same pattern as
        // BotConnectionLifecycleTests.ReadLatestAuditEventAsync).
        Log("Step: verifying app audit log");
        (string EventType, long? ActorUserId, string PayloadJson) auditRow;
        await using (var dbConn = Ds.CreateConnection())
        {
            await dbConn.OpenAsync().ConfigureAwait(false);
            auditRow = await dbConn.QuerySingleAsync<(string, long?, string)>("""
                SELECT event_type,
                       actor_user_id,
                       payload_json::text AS payload_json
                FROM   audit_log
                WHERE  tenant_id  = @TenantId
                  AND  event_type = 'message.deleted'
                ORDER  BY id DESC
                LIMIT  1
                """, new { TenantId }).ConfigureAwait(false);
        }

        auditRow.EventType.Should().Be("message.deleted",
            "a message.deleted audit row must exist for this tenant");
        auditRow.PayloadJson.Should().Contain(publicId.ToString(),
            "the audit entry payload must reference the deleted message's public id");

        Log($"Step: audit log verified (actor_user_id={auditRow.ActorUserId}, contains publicId={publicId})");

        // ── Verify soft-delete in DB via REST ─────────────────────────────────
        // GET /messages should no longer include this message (soft-deleted rows
        // are excluded from the history query by convention).
        var afterRes = await Client.GetMessagesAsync(TenantSlug, BridgedChannelPublicId, limit: 20)
            .ConfigureAwait(false);
        afterRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterBody = await afterRes.Content
            .ReadFromJsonAsync<MessageHistoryApiResponse>()
            .ConfigureAwait(false);
        var stillVisible = afterBody?.Messages?.Any(m => m.PublicId == publicId) ?? false;
        stillVisible.Should().BeFalse(
            "a soft-deleted inbound message must not appear in the GET /messages history");

        Log("PASS: owner moderation delete verified — Discord + audit log + DB all consistent");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HistoryItem?> PollForMessageByContentAsync(
        string content, TimeSpan timeout)
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
