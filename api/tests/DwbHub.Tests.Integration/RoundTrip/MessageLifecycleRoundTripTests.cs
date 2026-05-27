using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using DwbHub.Application.Bot;
using DwbHub.Application.Messaging;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Bot;
using DwbHub.Infrastructure.Messaging;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.RoundTrip;

/// <summary>
/// Phase 1: Discord round-trip tests.
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
/// random prefix ([dwbhub-roundtrip:&lt;runId&gt;]) so leftover artifacts are
/// identifiable. Teardown sweeps those markers best-effort.
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "DiscordRoundTrip")]
public sealed class MessageLifecycleRoundTripTests : IAsyncLifetime
{
    // ── Marker prefix — unique per test-run, used for cleanup ────────────────
    private static readonly string MarkerPrefix =
        $"[dwbhub-roundtrip:{Guid.NewGuid():N}]";

    // ── Required env vars ────────────────────────────────────────────────────
    private string? _botToken;
    private ulong _discordGuildId;
    private ulong _testChannelDiscordId;
    private bool _envOk;

    // ── Fixed keys — acceptable because tests run serial under one collection ─
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    // Each test run generates a real 32-byte encryption key (AES-256) rather than
    // a fixed constant: the real AesGcmBotTokenEncryptor requires a valid key, and
    // a hard-coded all-zero key is fine for serial tests (no parallelism, no key
    // rotation exercised). Still: generate it fresh per factory so rotate semantics
    // are correct.
    private static readonly string Base64EncKey =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    // ── Infrastructure ───────────────────────────────────────────────────────
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _ds = null!;
    private TenantRepository _tenants = null!;
    private UserRepository _users = null!;
    private GuildRepository _guilds = null!;
    private GuildChannelRepository _channels = null!;

    private DwbHubRoundTripTestFactory _factory = null!;
    private HttpClient _client = null!;

    // ── Verification client (reads from Discord, bypasses app stack) ─────────
    private DiscordRestChannelClient _verifyClient = null!;
    private HttpClient _verifyHttp = null!;

    // ── Test state set during InitializeAsync ─────────────────────────────────
    private string _tenantSlug = null!;
    private JwtIssuer _issuer = null!;
    private string _ownerJwt = null!;
    private Guid _bridgedChannelPublicId;
    private long _tenantId;

    // Webhook credentials recovered from DB so we can call edit/delete directly.
    private ulong _webhookId;
    private string _webhookToken = null!;
    private IChannelWebhookCipher _webhookCipher = null!;

    public MessageLifecycleRoundTripTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
    }

    // ── IAsyncLifetime ───────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // 1. Read env — skip-flag if not all present.
        _botToken = Environment.GetEnvironmentVariable("DISCORD_DEV_BOT_TOKEN");
        var guildIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_GUILD_ID");
        var channelIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_CHANNEL_ID");

        _envOk = !string.IsNullOrWhiteSpace(_botToken)
            && !string.IsNullOrWhiteSpace(guildIdStr) && ulong.TryParse(guildIdStr, out _discordGuildId)
            && !string.IsNullOrWhiteSpace(channelIdStr) && ulong.TryParse(channelIdStr, out _testChannelDiscordId);

        if (!_envOk) return; // All three tests will Skip.

        // 2. Fresh DB.
        await _fixture.ResetAsync().ConfigureAwait(false);

        // 3. Set env vars the app host reads on startup.
        _tenantSlug = "roundtrip";
        var uniqueLogDir = Path.Combine(
            Path.GetTempPath(), "dwbhub-test-logs", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", uniqueLogDir);
        Environment.SetEnvironmentVariable("DWBHUB_DB_CONNECTION", _fixture.ConnectionString);
        Environment.SetEnvironmentVariable("DWBHUB_JWT_SECRET", Base64JwtKey);
        Environment.SetEnvironmentVariable("DWBHUB_ENCRYPTION_KEY", Base64EncKey);
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_PORT", "11025");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_FROM", "noreply@test.local");
        Environment.SetEnvironmentVariable("DWBHUB_PUBLIC_BASE_URL", "http://localhost:5173");
        Environment.SetEnvironmentVariable("DWBHUB_BOOTSTRAP_TOKEN_FILE", Path.GetTempFileName());
        // CRITICAL: do NOT set DWBHUB_DISCORD_TEST_MODE — production code path only.

        // 4. Repositories for seeding.
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var connFactory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(connFactory);
        _users = new UserRepository(connFactory);
        _guilds = new GuildRepository(connFactory);
        _channels = new GuildChannelRepository(connFactory);
        _issuer = new JwtIssuer(Base64JwtKey);

        // 5. Seed: tenant + owner user.
        _tenantId = await _tenants.CreateAsync(name: "Round-Trip Tenant", slug: _tenantSlug)
            .ConfigureAwait(false);
        var ownerUid = await _users.CreateAsync(new User(
            Id: 0, TenantId: _tenantId,
            Email: "owner@roundtrip.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: new BCryptPasswordHasher().Hash("pw"),
            DisplayName: "RTOwner",
            Role: UserRole.Owner,
            IsActive: true,
            CreatedAt: default,
            UpdatedAt: default)).ConfigureAwait(false);
        var ownerUser = new User(ownerUid, _tenantId, "owner@roundtrip.test",
            DateTimeOffset.UtcNow, "", "RTOwner", UserRole.Owner, true, default, default);
        var tenantEntity = new Tenant(_tenantId, "Round-Trip Tenant", _tenantSlug, "en", default, default);
        _ownerJwt = _issuer.Issue(ownerUser, tenantEntity);

        // 6. Seed guild using the real Discord guild ID.
        var discordGuildIdStr = _discordGuildId.ToString();
        var (guildId, guildPublicId) = await _guilds.CreateAsync(
            tenantId: _tenantId,
            discordGuildId: discordGuildIdStr,
            displayName: "RT Guild",
            registeredByUserId: ownerUid).ConfigureAwait(false);

        // 7. Start factory + client.
        _factory = new DwbHubRoundTripTestFactory();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _ownerJwt);

        // 8. Resolve the webhook cipher from DI so we can decrypt the webhook token
        //    after bridging (needed for edit/delete direct-REST verification).
        _webhookCipher = _factory.Services.GetRequiredService<IChannelWebhookCipher>();

        // 9. Set real bot credentials via API.
        var credRes = await _client.PutAsJsonAsync(
            $"/api/t/{_tenantSlug}/guilds/{guildPublicId:D}/bot-credentials",
            new { token = _botToken }).ConfigureAwait(false);
        credRes.StatusCode.Should().BeOneOf(
            [HttpStatusCode.NoContent, HttpStatusCode.OK],
            "PUT bot-credentials must succeed for the round-trip to work");

        // 10. Activate guild — triggers OnGuildActivatedAsync → BotConnectionManager connects.
        var activateRes = await _client.PostAsync(
            $"/api/t/{_tenantSlug}/guilds/{guildPublicId:D}/activate", null).ConfigureAwait(false);
        activateRes.StatusCode.Should().Be(HttpStatusCode.NoContent, "guild activate must succeed");

        // 11. Wait for bot to reach Connected state.
        var manager = _factory.Services.GetRequiredService<BotConnectionManager>();
        await WaitForBotConnectedAsync(manager, guildId, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        // 12. Sync channels from Discord.
        var syncRes = await _client.PostAsync(
            $"/api/t/{_tenantSlug}/guilds/{guildPublicId:D}/channels/sync", null).ConfigureAwait(false);
        syncRes.StatusCode.Should().Be(HttpStatusCode.NoContent, "channel sync must succeed");

        // 13. Find the target channel in DB by its Discord snowflake.
        var targetChannel = await _channels.GetByDiscordIdAsync(
            _tenantId, (long)_testChannelDiscordId).ConfigureAwait(false);
        targetChannel.Should().NotBeNull(
            $"channel {_testChannelDiscordId} must be visible to the bot after sync");
        _bridgedChannelPublicId = targetChannel!.PublicId;

        // 14. Bridge the channel — creates a Discord webhook.
        var bridgeRes = await _client.PostAsync(
            $"/api/t/{_tenantSlug}/channels/{_bridgedChannelPublicId:D}/bridge", null).ConfigureAwait(false);
        bridgeRes.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "bridge must succeed so the round-trip send can post a message");

        // 15. Read webhook credentials from DB for direct-REST edit/delete.
        var channelRow = await _channels.GetByPublicIdAsync(_tenantId, _bridgedChannelPublicId)
            .ConfigureAwait(false);
        channelRow.Should().NotBeNull();
        await using var dbConn = _ds.CreateConnection();
        await dbConn.OpenAsync().ConfigureAwait(false);
        var webhookRow = await dbConn.QuerySingleOrDefaultAsync("""
            SELECT discord_webhook_id, ciphertext, nonce, auth_tag, key_version
            FROM channel_webhooks
            WHERE tenant_id = @TenantId AND channel_id = @ChannelId
            """,
            new { TenantId = _tenantId, ChannelId = channelRow!.Id }).ConfigureAwait(false);
        ((object?)webhookRow).Should().NotBeNull("webhook row must exist after bridging");
        _webhookId = (ulong)(long)webhookRow!.discord_webhook_id;
        var envelope = new ChannelWebhookEnvelope(
            Ciphertext: (byte[])webhookRow.ciphertext,
            Nonce: (byte[])webhookRow.nonce,
            AuthTag: (byte[])webhookRow.auth_tag,
            KeyVersion: (int)webhookRow.key_version);
        _webhookToken = _webhookCipher.Decrypt(envelope);

        // 16. Stand up a plain DiscordRestChannelClient for verification (bypass app stack).
        _verifyHttp = new HttpClient();
        _verifyClient = new DiscordRestChannelClient(
            _verifyHttp,
            NullLogger<DiscordRestChannelClient>.Instance);
    }

    public async Task DisposeAsync()
    {
        // Best-effort cleanup: remove all messages in the test channel starting with MarkerPrefix.
        if (_envOk && _botToken is not null && _verifyClient is not null && _webhookId != 0)
        {
            try
            {
                var messages = await _verifyClient.GetMessagesAsync(
                    _botToken, _testChannelDiscordId, beforeSnowflake: null, limit: 50,
                    CancellationToken.None).ConfigureAwait(false);

                foreach (var msg in messages)
                {
                    if (msg.Content.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                    {
                        try
                        {
                            // Use bot DELETE (REST) if webhook still valid.
                            await _verifyClient.DeleteWebhookMessageAsync(
                                _webhookId, _webhookToken, msg.Id, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Ignore — channel may need manual cleanup.
                        }
                    }
                }
            }
            catch
            {
                // Swallow — cleanup failure must not mask test assertion.
            }
        }

        _client?.Dispose();
        _verifyHttp?.Dispose(); // DiscordRestChannelClient does not own the HttpClient
        if (_factory is not null) await _factory.DisposeAsync().ConfigureAwait(false);
        _ds?.Dispose();
    }

    // ── Test 1: Send ─────────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Send_message_via_api_appears_in_real_discord_channel()
    {
        Skip.IfNot(_envOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        var content = TestContent("send-test-1");

        // POST via the full app stack.
        var res = await _client.PostAsJsonAsync(
            $"/api/t/{_tenantSlug}/channels/{_bridgedChannelPublicId:D}/messages",
            new { content }).ConfigureAwait(false);
        res.StatusCode.Should().Be(HttpStatusCode.Created,
            "the full-stack POST must return 201 Created");

        var body = await res.Content.ReadFromJsonAsync<SendMessageBody>().ConfigureAwait(false);
        body.Should().NotBeNull();

        // Verify: the message is visible in the real Discord channel.
        var found = await PollForDiscordMessageAsync(
            _testChannelDiscordId, content, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        found.Should().NotBeNull(
            "the API must have written through to Discord so the message appears there");

        // Discord snowflake in the response must match what Discord returned.
        found!.Id.Should().Be((ulong)body!.DiscordMessageId,
            "the discord_message_id in the API response must match the actual Discord snowflake");
    }

    // ── Test 2: Edit ─────────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Edit_message_via_webhook_updates_real_discord_message()
    {
        Skip.IfNot(_envOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        // Setup: send a message first.
        var initialContent = TestContent("edit-test-initial");
        var postRes = await _client.PostAsJsonAsync(
            $"/api/t/{_tenantSlug}/channels/{_bridgedChannelPublicId:D}/messages",
            new { content = initialContent }).ConfigureAwait(false);
        postRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var postBody = await postRes.Content.ReadFromJsonAsync<SendMessageBody>().ConfigureAwait(false);
        postBody.Should().NotBeNull();
        var messageSnowflake = (ulong)postBody!.DiscordMessageId;

        // Verify it arrived.
        var sent = await PollForDiscordMessageAsync(
            _testChannelDiscordId, initialContent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        sent.Should().NotBeNull("message must be present in Discord before editing");

        // Edit directly via Discord REST (webhook edit path).
        var editedContent = TestContent("edit-test-edited");
        var edited = await _verifyClient.EditWebhookMessageAsync(
            _webhookId, _webhookToken, messageSnowflake, editedContent,
            CancellationToken.None).ConfigureAwait(false);
        edited.Content.Should().Be(editedContent,
            "Discord must reflect the new content after webhook edit");
        edited.Id.Should().Be(messageSnowflake, "the same message snowflake must be returned");

        // Secondary verification: poll Discord until the edit appears.
        var verified = await PollForDiscordMessageAsync(
            _testChannelDiscordId, editedContent, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        verified.Should().NotBeNull(
            "the edited content must be visible in Discord REST after the edit");
    }

    // ── Test 3: Delete ───────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Delete_message_via_webhook_removes_real_discord_message()
    {
        Skip.IfNot(_envOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        // Setup: send a message first.
        var content = TestContent("delete-test");
        var postRes = await _client.PostAsJsonAsync(
            $"/api/t/{_tenantSlug}/channels/{_bridgedChannelPublicId:D}/messages",
            new { content }).ConfigureAwait(false);
        postRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var postBody = await postRes.Content.ReadFromJsonAsync<SendMessageBody>().ConfigureAwait(false);
        postBody.Should().NotBeNull();
        var messageSnowflake = (ulong)postBody!.DiscordMessageId;

        // Verify it arrived.
        var sent = await PollForDiscordMessageAsync(
            _testChannelDiscordId, content, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        sent.Should().NotBeNull("message must be present in Discord before deleting");

        // Delete via Discord REST (webhook delete path).
        var deleted = await _verifyClient.DeleteWebhookMessageAsync(
            _webhookId, _webhookToken, messageSnowflake, CancellationToken.None).ConfigureAwait(false);
        deleted.Should().BeTrue("DeleteWebhookMessageAsync must return true on success");

        // Mark as gone so teardown skips it.
        await AssertDiscordMessageGoneAsync(
            _testChannelDiscordId, messageSnowflake, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    // ── Helper methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a test-message content string with the run-unique marker prefix.
    /// Teardown uses the prefix to identify leftover artifacts.
    /// </summary>
    private static string TestContent(string suffix) => $"{MarkerPrefix} {suffix}";

    /// <summary>
    /// Polls the Discord channel REST endpoint until a message whose content contains
    /// <paramref name="contentSubstring"/> is found, or the timeout elapses.
    /// Returns null on timeout.
    /// </summary>
    private async Task<DiscordMessageInfo?> PollForDiscordMessageAsync(
        ulong channelId,
        string contentSubstring,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var messages = await _verifyClient.GetMessagesAsync(
                    _botToken!, channelId, beforeSnowflake: null, limit: 50,
                    CancellationToken.None).ConfigureAwait(false);

                var match = messages.FirstOrDefault(m =>
                    m.Content.Contains(contentSubstring, StringComparison.Ordinal));
                if (match is not null) return match;
            }
            catch
            {
                // Transient — keep polling.
            }
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Polls the Discord channel REST endpoint until the message is absent (deleted),
    /// or asserts failure on timeout.
    /// </summary>
    private async Task AssertDiscordMessageGoneAsync(
        ulong channelId,
        ulong messageId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var messages = await _verifyClient.GetMessagesAsync(
                    _botToken!, channelId, beforeSnowflake: null, limit: 50,
                    CancellationToken.None).ConfigureAwait(false);

                var stillPresent = messages.Any(m => m.Id == messageId);
                if (!stillPresent) return; // success
            }
            catch
            {
                // Transient — keep polling.
            }
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        // If we reach here, the message is still present — fail the test.
        true.Should().BeFalse(
            $"Discord message {messageId} was still present in channel {channelId} " +
            $"after {timeout.TotalSeconds:F0}s — delete did not propagate");
    }

    /// <summary>
    /// Polls <see cref="BotConnectionManager.GetState"/> until the guild reports
    /// <see cref="BotConnectionState.Connected"/>, or throws on timeout.
    /// </summary>
    private static async Task WaitForBotConnectedAsync(
        BotConnectionManager manager,
        long guildId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (manager.GetState(guildId) == BotConnectionState.Connected)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        var finalState = manager.GetState(guildId);
        throw new TimeoutException(
            $"Bot for guild {guildId} did not reach Connected state within {timeout.TotalSeconds:F0}s " +
            $"(final state: {finalState?.ToString() ?? "null"}).");
    }
}

// ── Response shapes ───────────────────────────────────────────────────────────

file sealed record SendMessageBody(long Id, long DiscordMessageId, DateTimeOffset SentAt);
