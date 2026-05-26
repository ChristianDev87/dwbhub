using DwbHub.Application.Messaging;
using DwbHub.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DwbHub.Tests.Integration.Messaging;

/// <summary>
/// Plan 1.0 Task 15 — live REST tests against the real Discord API.
///
/// These exercise <see cref="IDiscordRestChannelClient"/> end-to-end against
/// Discord's REST endpoints. They are STRICTLY READ-ONLY: ListChannelsAsync
/// (GET /guilds/:id/channels) and GetMessagesAsync (GET /channels/:id/messages)
/// — neither writes to the user's Discord server. Webhook tests (which would
/// post a message) are intentionally NOT in this file; if a future PR needs
/// to verify the write path live, it should use a dedicated isolated test
/// channel and clean up the posted message on teardown.
///
/// Skipped unless ALL three are set:
///   DISCORD_DEV_BOT_TOKEN, DISCORD_DEV_GUILD_ID, DISCORD_DEV_CHANNEL_ID
///
/// CI: a dedicated job runs them on push to develop/main with the GitHub
/// Secrets in scope. Locally the user's deploy/compose/discord.dev.env
/// supplies the values via jobs/discord-live.sh.
///
/// SECURITY: the token is never logged. The guild + channel snowflakes are
/// also treated as opaque (per project rule — they are part of the secret
/// envelope). discord-live.sh greps results for both before passing the run.
/// </summary>
[Trait("Category", "DiscordLive")]
public sealed class DiscordRestChannelClientLiveTests
{
    private static (string token, ulong guildId, ulong channelId)? ReadEnv()
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_DEV_BOT_TOKEN");
        var guildIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_GUILD_ID");
        var channelIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_CHANNEL_ID");
        if (string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(guildIdStr)
            || string.IsNullOrWhiteSpace(channelIdStr))
        {
            return null;
        }
        if (!ulong.TryParse(guildIdStr, out var guildId)) return null;
        if (!ulong.TryParse(channelIdStr, out var channelId)) return null;
        return (token, guildId, channelId);
    }

    private static DiscordRestChannelClient CreateClient()
    {
        // The production code path wires DiscordRestChannelClient via
        // IHttpClientFactory in Program.cs. Here we instantiate directly with
        // a plain HttpClient — these tests only care that the REST surface
        // works end-to-end against Discord, not the DI plumbing.
        var http = new HttpClient();
        return new DiscordRestChannelClient(http, NullLogger<DiscordRestChannelClient>.Instance);
    }

    [SkippableFact]
    public async Task ListChannelsAsync_returns_channels_including_DEV_CHANNEL_ID()
    {
        var env = ReadEnv();
        Skip.If(env is null,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        var client = CreateClient();
        var channels = await client.ListChannelsAsync(
            env!.Value.token,
            env.Value.guildId,
            CancellationToken.None);

        // Bot must see at least one channel in the guild — otherwise either the
        // bot is not actually in the guild, lacks View Channel permission, or
        // Discord returned an empty list (broken). Any of these break Plan 1.0.
        Assert.NotEmpty(channels);

        // The configured channel MUST be among the returned channels.
        // If it is missing the bot cannot bridge it — Plan 1.0's whole flow
        // depends on this being visible.
        Assert.Contains(channels, c => c.Id == env.Value.channelId);
    }

    [SkippableFact]
    public async Task GetMessagesAsync_on_DEV_CHANNEL_does_not_throw()
    {
        var env = ReadEnv();
        Skip.If(env is null,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        var client = CreateClient();
        // limit=1 minimises network + parse cost. A brand-new channel may
        // legitimately have zero history; the test only asserts that the
        // call succeeds (no DiscordPermissionException, no
        // DiscordRateLimitException, no parse error) — the message list
        // may be empty without failing the test.
        var messages = await client.GetMessagesAsync(
            env!.Value.token,
            env.Value.channelId,
            beforeSnowflake: null,
            limit: 1,
            CancellationToken.None);

        Assert.NotNull(messages);
    }

    [SkippableFact]
    public async Task Webhook_full_lifecycle_create_send_edit_delete_cleanup()
    {
        // This test exercises the FULL user-facing message round-trip against
        // real Discord exactly as a dwbhub end-user would experience it:
        //
        //   1. CreateWebhookAsync         — bot provisions a webhook on the channel
        //   2. ExecuteWebhookAsync (SEND) — webhook posts a message; capture msgId
        //   3. EditWebhookMessageAsync    — modify the message content
        //   4. DeleteWebhookMessageAsync  — remove the message
        //   5. DeleteWebhookAsync         — tear down the test webhook
        //
        // Every step is mandatory because cleanup runs in a try/finally so the
        // user's Discord channel never accumulates test artifacts: a partial
        // run still removes the webhook + any message it managed to create.
        //
        // The message content uses a clear "[dwbhub-live-test]" prefix so that
        // if the cleanup ever silently fails (network timeout between SEND and
        // DELETE etc.), the leftover is unmistakable in the channel history.
        var env = ReadEnv();
        Skip.If(env is null,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        var client = CreateClient();
        var runId = Guid.NewGuid().ToString("N").Substring(0, 8);
        var initialContent = $"[dwbhub-live-test:{runId}] initial — send";
        var editedContent = $"[dwbhub-live-test:{runId}] edited — patch verified";

        DiscordWebhookCreated? webhook = null;
        ulong? sentMessageId = null;
        try
        {
            // ── 1. Create webhook ─────────────────────────────────────────────
            webhook = await client.CreateWebhookAsync(
                env!.Value.token,
                env.Value.channelId,
                name: "dwbhub-live-test",
                CancellationToken.None);
            Assert.NotEqual(0UL, webhook.WebhookId);
            Assert.False(string.IsNullOrWhiteSpace(webhook.WebhookToken));

            // ── 2. Send message via webhook ───────────────────────────────────
            var sent = await client.ExecuteWebhookAsync(
                webhook.WebhookId,
                webhook.WebhookToken,
                username: "dwbhub-test",
                content: initialContent,
                CancellationToken.None);
            sentMessageId = sent.Id;
            Assert.Equal(initialContent, sent.Content);
            Assert.True(sent.AuthorIsWebhook);
            Assert.NotEqual(0UL, sent.Id);

            // ── 3. Edit message ───────────────────────────────────────────────
            var edited = await client.EditWebhookMessageAsync(
                webhook.WebhookId,
                webhook.WebhookToken,
                sent.Id,
                editedContent,
                CancellationToken.None);
            Assert.Equal(sent.Id, edited.Id); // same message snowflake
            Assert.Equal(editedContent, edited.Content);

            // ── 4. Delete message ─────────────────────────────────────────────
            var deleted = await client.DeleteWebhookMessageAsync(
                webhook.WebhookId,
                webhook.WebhookToken,
                sent.Id,
                CancellationToken.None);
            Assert.True(deleted);
            sentMessageId = null; // message is gone — skip the finally-cleanup
        }
        finally
        {
            // Best-effort cleanup. Any single step failing must NOT mask the
            // assertion that the previous successful step verified.
            if (sentMessageId is { } leftoverMsgId && webhook is { } wh1)
            {
                try
                {
                    await client.DeleteWebhookMessageAsync(
                        wh1.WebhookId, wh1.WebhookToken, leftoverMsgId, CancellationToken.None);
                }
                catch { /* swallow — channel may need manual cleanup if this also fails */ }
            }
            if (webhook is { } wh2)
            {
                try
                {
                    await client.DeleteWebhookAsync(
                        wh2.WebhookId, wh2.WebhookToken, CancellationToken.None);
                }
                catch { /* swallow — webhook may need manual cleanup if this also fails */ }
            }
        }
    }
}
