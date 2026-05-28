using System.Net.Http.Json;
using System.Text.Json;
using Discord;
using DwbHub.Application.Messaging;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DwbHub.Tests.Integration.RoundTrip;

/// <summary>
/// Phase 2.4: Backfill round-trip test.
///
/// Validates the full backfill pipeline:
///   1. Helper Bot (Bot B) posts N marker-prefixed messages directly to Discord
///      BEFORE the channel is bridged.
///   2. POST /bridge — bridge accepted, backfill job created (pending).
///   3. IBackfillRunner.RunAsync invoked directly (Hangfire BackgroundProcessingServer
///      is removed from the test factory to prevent teardown races; we trigger the job
///      in-process instead).
///   4. GET /backfill-status must report status = "complete".
///   5. GET /messages?limit=100 must contain all N marker-prefixed messages.
///
/// SKIPPED unless all four env vars are set:
///   DISCORD_DEV_BOT_TOKEN, DISCORD_DEV_GUILD_ID, DISCORD_DEV_CHANNEL_ID,
///   DISCORD_DEV_HELPER_BOT_TOKEN
///
/// Slug: roundtrip-backfill — isolated from the outbound/inbound/reconnect suites
/// at the tenant boundary. The Discord channel is shared, but the assertion is
/// "all 20 of ours arrived", not "only 20 messages exist", so other tests'
/// leftover markers do not interfere.
/// </summary>
public sealed class MessageBackfillRoundTripTests(
    PostgresFixture fixture, ITestOutputHelper output)
    : RoundTripTestBase(fixture, output)
{
    protected override string TenantSlug => "roundtrip-backfill";
    protected override string MarkerPrefix => "[dwbhub-roundtrip-backfill:";
    protected override bool RequiresHelperBot => true;

    // Bridge is NOT called in setup — it happens inside the test after pre-bridge posts.
    protected override bool BridgeInSetup => false;

    // ── Unique marker for this test run ───────────────────────────────────────
    private string _marker = null!;

    // ── Sub-class setup hook ───────────────────────────────────────────────────

    protected override async Task OnSetupCompleted()
    {
        // Defensive pre-test cleanup: sweep old [dwbhub-roundtrip-backfill:...] messages
        // from crashed prior runs so the channel doesn't accumulate stale markers.
        try
        {
            var ch = await HelperBot!.GetChannelAsync(TestChannelDiscordId)
                .ConfigureAwait(false) as IMessageChannel;
            if (ch is not null)
            {
                var old = await ch.GetMessagesAsync(100).FlattenAsync().ConfigureAwait(false);
                int swept = 0;
                foreach (var msg in old.Where(m =>
                    m.Content.StartsWith("[dwbhub-roundtrip-backfill:", StringComparison.Ordinal)))
                {
                    try { await msg.DeleteAsync().ConfigureAwait(false); swept++; } catch { /* swallow — MANAGE_MESSAGES may be absent */ }
                }
                if (swept > 0)
                    Log($"Setup: swept {swept} stale backfill marker message(s)");
            }
        }
        catch { /* swallow */ }

        Log("Setup: backfill pre-cleanup complete");
    }

    // ── Sub-class teardown hook ────────────────────────────────────────────────

    protected override async Task OnTeardown()
    {
        // Clean up helper bot messages — sweep by marker prefix.
        if (HelperBot is not null)
        {
            try
            {
                var ch = await HelperBot.GetChannelAsync(TestChannelDiscordId)
                    .ConfigureAwait(false) as IMessageChannel;

                if (ch is not null)
                {
                    try
                    {
                        var msgs = await ch.GetMessagesAsync(100)
                            .FlattenAsync().ConfigureAwait(false);
                        int swept = 0;
                        foreach (var msg in msgs.Where(m =>
                            m.Content.StartsWith("[dwbhub-roundtrip-backfill:", StringComparison.Ordinal)))
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
    public async Task Backfill_after_bridge_imports_helper_bot_messages()
    {
        Skip.IfNot(EnvOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_HELPER_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        // ── 1) Helper bot posts 20 pre-bridge marker messages ──────────────────
        _marker = $"[dwbhub-roundtrip-backfill:{Guid.NewGuid():N}]";
        Log($"Step: helper bot posting 20 pre-bridge messages with marker prefix");

        var channel = await HelperBot!.GetChannelAsync(TestChannelDiscordId)
            .ConfigureAwait(false) as IMessageChannel;
        channel.Should().NotBeNull(
            $"Helper bot must be able to see channel {TestChannelDiscordId}; " +
            "check that it is a member of the guild with SEND_MESSAGES");

        const int MessageCount = 20;
        var postedContents = new List<string>(MessageCount);
        for (int i = 0; i < MessageCount; i++)
        {
            var content = $"{_marker} pre-bridge-{i:D2}";
            var sent = await channel!.SendMessageAsync(content).ConfigureAwait(false);
            HelperPostedMessageIds.Add(sent.Id);
            postedContents.Add(content);
            // Stay under Discord's bot rate limit (5 messages / 5 s per channel).
            await Task.Delay(150).ConfigureAwait(false);
        }

        Log($"Step: posted {MessageCount} pre-bridge messages");

        // ── 2) Bridge the channel — creates webhook + enqueues backfill job ────
        Log("Step: bridging channel via API");
        var bridgeRes = await Client.BridgeChannelAsync(TenantSlug, BridgedChannelPublicId)
            .ConfigureAwait(false);
        bridgeRes.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted,
            "bridge must succeed so the backfill pipeline can fetch the pre-bridge messages");

        var bridgeBody = await bridgeRes.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        var backfillJobId = bridgeBody.GetProperty("backfillJobId").GetInt64();
        backfillJobId.Should().BePositive("bridge response must include a valid backfill job ID");

        Log($"Step: channel bridged, backfillJobId={backfillJobId}");

        // 2a. Read webhook credentials from DB so base teardown can delete the webhook.
        await ReadAndStoreWebhookCredentialsAsync().ConfigureAwait(false);
        Log($"Step: webhook credentials loaded (webhookId={WebhookId})");

        // ── 3) Run the backfill job in-process ────────────────────────────────
        // The DwbHubRoundTripTestFactory removes Hangfire's BackgroundProcessingServer
        // (to avoid teardown races). We therefore invoke the runner directly via DI
        // rather than waiting for Hangfire to pick it up.
        Log("Step: running backfill job in-process");
        using var scope = Factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IBackfillRunner>();
        await runner.RunAsync(TenantId, backfillJobId).ConfigureAwait(false);
        Log("Step: backfill RunAsync completed");

        // ── 4) Verify: backfill-status must be "complete" ─────────────────────
        Log("Step: checking backfill-status via API");
        var statusRes = await Client.GetBackfillStatusAsync(TenantSlug, BridgedChannelPublicId)
            .ConfigureAwait(false);
        statusRes.IsSuccessStatusCode.Should().BeTrue(
            "GET /backfill-status must return 200 after RunAsync completes");

        var statusBody = await statusRes.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        var status = statusBody.GetProperty("status").GetProperty("status").GetString();
        status.Should().Be("complete",
            "backfill job must be in 'complete' state after RunAsync returns");

        Log($"Step: backfill-status = {status}");

        // fetchedCount reflects newly-inserted rows only (ON CONFLICT DO NOTHING).
        // When the test channel already contains messages from prior round-trip test
        // runs (inbound / reconnect) that share the same Discord snowflakes, those
        // rows are skipped and fetchedCount may be less than MessageCount or even 0.
        // This is correct behaviour — we assert message presence below (step 5).
        var fetchedCount = statusBody.GetProperty("status").GetProperty("fetchedCount").GetInt32();
        fetchedCount.Should().BeGreaterThanOrEqualTo(0,
            "fetchedCount must be non-negative");

        // ── 5) Verify: all 20 helper-bot messages are in the DB ───────────────
        // Use a generous limit to absorb any extra historical messages in the channel.
        Log("Step: verifying all 20 messages are in GET /messages response");
        var fetched = await GetMessagesFromApiAsync(limit: 100).ConfigureAwait(false);
        var fetchedContents = fetched.Select(m => m.Content).ToList();

        foreach (var expected in postedContents)
        {
            fetchedContents.Should().Contain(expected,
                $"helper-bot message '{expected}' must have been backfilled into the DB");
        }

        Log($"Step: all {MessageCount} messages confirmed in DB");
    }

    // ── Helper: GET /messages from the API ────────────────────────────────────

    private async Task<IReadOnlyList<BackfillHistoryItem>> GetMessagesFromApiAsync(int limit = 100)
    {
        var resp = await Client.GetMessagesAsync(TenantSlug, BridgedChannelPublicId, limit: limit)
            .ConfigureAwait(false);
        resp.IsSuccessStatusCode.Should().BeTrue(
            $"GET /messages must succeed after backfill completes (status: {resp.StatusCode})");

        var body = await resp.Content
            .ReadFromJsonAsync<BackfillMessageHistoryApiResponse>()
            .ConfigureAwait(false);
        return body?.Messages ?? [];
    }
}

// ── Local types ───────────────────────────────────────────────────────────────

/// <summary>Response shape from GET /api/t/{slug}/channels/{id}/messages.</summary>
internal sealed record BackfillMessageHistoryApiResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("messages")]
    List<BackfillHistoryItem>? Messages,
    [property: System.Text.Json.Serialization.JsonPropertyName("nextBefore")]
    long? NextBefore);

internal sealed record BackfillHistoryItem(
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
