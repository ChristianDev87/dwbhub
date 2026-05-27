using System.Net;
using System.Net.Http.Json;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DwbHub.Tests.Integration.RoundTrip;

/// <summary>
/// Phase 1: Discord round-trip tests — outbound message lifecycle.
///
/// These tests close the gap between two existing test layers:
///   • Integration tests (app stack + fake Discord)
///   • Discord-live tests (real Discord, but NO app stack)
///
/// Round-trip tests exercise BOTH: an HTTP request flows through the full
/// ASP.NET pipeline (DI + AES encryption + BotConnectionManager +
/// DiscordRestChannelClient) and the outcome is verified by querying the
/// real Discord REST API.
///
/// SKIPPED unless all three env vars are set:
///   DISCORD_DEV_BOT_TOKEN, DISCORD_DEV_GUILD_ID, DISCORD_DEV_CHANNEL_ID
///
/// CI: a dedicated job runs them on PR → main via discord-roundtrip.sh.
///
/// SECURITY: The bot token is never printed. Content markers use a per-run
/// random prefix ([dwbhub-roundtrip-outbound:&lt;runId&gt;]) so leftover artifacts
/// are identifiable. Teardown sweeps those markers best-effort.
/// </summary>
public sealed class MessageLifecycleRoundTripTests(
    PostgresFixture fixture, ITestOutputHelper output)
    : RoundTripTestBase(fixture, output)
{
    // ── Marker prefix — unique per test-run, used for cleanup ────────────────
    private static readonly string RunMarker =
        $"[dwbhub-roundtrip-outbound:{Guid.NewGuid():N}]";

    protected override string TenantSlug => "roundtrip-outbound";
    protected override string MarkerPrefix => "[dwbhub-roundtrip-outbound:";
    protected override bool RequiresHelperBot => false;

    // ── Test 1: Send ─────────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Send_message_via_api_appears_in_real_discord_channel()
    {
        Skip.IfNot(EnvOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        var content = $"{RunMarker} send-test-1";

        Log("Step: posting message via API");
        var res = await Client.PostMessageAsync(
            TenantSlug, BridgedChannelPublicId, new { content }).ConfigureAwait(false);
        res.StatusCode.Should().Be(HttpStatusCode.Created,
            "the full-stack POST must return 201 Created");

        var body = await res.Content.ReadFromJsonAsync<SendMessageBody>().ConfigureAwait(false);
        body.Should().NotBeNull();
        Log($"Step: message returned 201, discordId={body!.DiscordMessageId}");

        // Verify: the message is visible in the real Discord channel.
        Log("Step: verifying in Discord");
        var found = await PollForDiscordMessageAsync(
            TestChannelDiscordId, content, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        found.Should().NotBeNull(
            "the API must have written through to Discord so the message appears there");
        Log("Step: found in Discord");

        // Discord snowflake in the response must match what Discord returned.
        found!.Id.Should().Be((ulong)body.DiscordMessageId,
            "the discord_message_id in the API response must match the actual Discord snowflake");
    }

    // ── Test 2: Edit ─────────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Edit_message_via_api_updates_real_discord_message()
    {
        Skip.IfNot(EnvOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        // Setup: send a message first through the full pipeline.
        var initialContent = $"{RunMarker} edit-test-initial";
        Log("Step: posting initial message via API");
        var postRes = await Client.PostMessageAsync(
            TenantSlug, BridgedChannelPublicId, new { content = initialContent })
            .ConfigureAwait(false);
        postRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var postBody = await postRes.Content.ReadFromJsonAsync<SendMessageBody>().ConfigureAwait(false);
        postBody.Should().NotBeNull();
        var messagePublicId = postBody!.PublicId;
        var messageSnowflake = (ulong)postBody.DiscordMessageId;

        // Verify the message arrived on Discord.
        var sent = await PollForDiscordMessageAsync(
            TestChannelDiscordId, initialContent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        sent.Should().NotBeNull("message must be present in Discord before editing");

        // Edit via the real PATCH endpoint (full app pipeline → DiscordRestChannelClient).
        var editedContent = $"{RunMarker} edit-test-edited";
        Log("Step: patching message via API");
        var patchRes = await Client.PatchMessageAsync(
            TenantSlug, BridgedChannelPublicId, messagePublicId, new { content = editedContent })
            .ConfigureAwait(false);
        patchRes.StatusCode.Should().Be(HttpStatusCode.OK,
            "PATCH /messages/{id} must return 200 OK after a successful edit");

        // Verification: poll Discord until the edited content appears.
        Log("Step: verifying edit in Discord");
        var verified = await PollForDiscordMessageAsync(
            TestChannelDiscordId, editedContent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        verified.Should().NotBeNull(
            "the edited content must be visible in Discord REST after the API edit");
        verified!.Id.Should().Be(messageSnowflake, "the same Discord snowflake must be present");
        Log("Step: edit confirmed in Discord");
    }

    // ── Test 3: Delete ───────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Delete_message_via_api_removes_real_discord_message()
    {
        Skip.IfNot(EnvOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        // Setup: send a message first through the full pipeline.
        var content = $"{RunMarker} delete-test";
        Log("Step: posting message for delete test via API");
        var postRes = await Client.PostMessageAsync(
            TenantSlug, BridgedChannelPublicId, new { content }).ConfigureAwait(false);
        postRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var postBody = await postRes.Content.ReadFromJsonAsync<SendMessageBody>().ConfigureAwait(false);
        postBody.Should().NotBeNull();
        var messagePublicId = postBody!.PublicId;
        var messageSnowflake = (ulong)postBody.DiscordMessageId;

        // Verify the message arrived on Discord.
        var sent = await PollForDiscordMessageAsync(
            TestChannelDiscordId, content, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        sent.Should().NotBeNull("message must be present in Discord before deleting");

        // Delete via the real DELETE endpoint (full app pipeline → DiscordRestChannelClient).
        Log("Step: deleting message via API");
        var deleteRes = await Client.DeleteMessageAsync(
            TenantSlug, BridgedChannelPublicId, messagePublicId).ConfigureAwait(false);
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "DELETE /messages/{id} must return 204 NoContent after a successful delete");

        // Verification: poll Discord until the message is gone.
        Log("Step: verifying delete in Discord");
        await AssertDiscordMessageGoneAsync(
            TestChannelDiscordId, messageSnowflake, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Log("Step: delete confirmed in Discord");
    }
}

// ── Response shapes ───────────────────────────────────────────────────────────

file sealed record SendMessageBody(long Id, Guid PublicId, long DiscordMessageId, DateTimeOffset SentAt);
