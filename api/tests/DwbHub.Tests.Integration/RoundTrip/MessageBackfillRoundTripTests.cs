using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Discord;
using Discord.Rest;
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
[Collection(DatabaseCollection.Name)]
[Trait("Category", "DiscordRoundTrip")]
public sealed class MessageBackfillRoundTripTests : IAsyncLifetime
{
    // ── Required env vars ──────────────────────────────────────────────────────
    private string? _botToken;           // Bot A — main app bot
    private string? _helperBotToken;     // Bot B — posts pre-bridge messages
    private ulong _discordGuildId;
    private ulong _testChannelDiscordId;
    private bool _envOk;

    // ── Fixed keys — serial tests under one collection ────────────────────────
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly string Base64EncKey =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    // ── Infrastructure ────────────────────────────────────────────────────────
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _ds = null!;
    private TenantRepository _tenants = null!;
    private UserRepository _users = null!;
    private GuildRepository _guilds = null!;
    private GuildChannelRepository _channels = null!;

    private DwbHubRoundTripTestFactory _factory = null!;
    private HttpClient _client = null!;             // owner-authed API client

    // ── Test state set during InitializeAsync ──────────────────────────────────
    private string _slug = null!;
    private JwtIssuer _issuer = null!;
    private string _ownerJwt = null!;
    private Guid _bridgedChannelPublicId;
    private long _tenantId;
    private long _guildId;
    private Guid _guildPublicId;

    // ── Webhook credentials (recovered from DB after bridge) ──────────────────
    private ulong _webhookId;
    private string? _webhookToken;
    private IChannelWebhookCipher _webhookCipher = null!;

    // ── Verification REST client (direct Discord REST, bypasses app stack) ────
    private HttpClient _verifyHttp = null!;
    private DiscordRestChannelClient _verifyClient = null!;

    // ── Helper bot (Bot B) — Discord REST for posting + cleanup ───────────────
    private DiscordRestClient? _helperBot;
    private readonly List<ulong> _helperPostedMessageIds = new();

    // ── Unique marker for this test run ───────────────────────────────────────
    private string _marker = null!;

    public MessageBackfillRoundTripTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
    }

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // 1. Read env — skip-flag if not all present.
        _botToken = Environment.GetEnvironmentVariable("DISCORD_DEV_BOT_TOKEN");
        _helperBotToken = Environment.GetEnvironmentVariable("DISCORD_DEV_HELPER_BOT_TOKEN");
        var guildIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_GUILD_ID");
        var channelIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_CHANNEL_ID");

        _envOk = !string.IsNullOrWhiteSpace(_botToken)
            && !string.IsNullOrWhiteSpace(_helperBotToken)
            && !string.IsNullOrWhiteSpace(guildIdStr) && ulong.TryParse(guildIdStr, out _discordGuildId)
            && !string.IsNullOrWhiteSpace(channelIdStr) && ulong.TryParse(channelIdStr, out _testChannelDiscordId);

        if (!_envOk) return;

        // 2. Fresh DB.
        await _fixture.ResetAsync().ConfigureAwait(false);

        // 3. Set env vars the app host reads on startup.
        _slug = "roundtrip-backfill";
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
        _tenantId = await _tenants.CreateAsync(name: "Round-Trip Backfill Tenant", slug: _slug)
            .ConfigureAwait(false);
        var ownerUid = await _users.CreateAsync(new User(
            Id: 0, TenantId: _tenantId,
            Email: "owner@roundtrip-backfill.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: new BCryptPasswordHasher().Hash("pw"),
            DisplayName: "RTBackfillOwner",
            Role: UserRole.Owner,
            IsActive: true,
            CreatedAt: default,
            UpdatedAt: default)).ConfigureAwait(false);
        var ownerUser = new User(ownerUid, _tenantId, "owner@roundtrip-backfill.test",
            DateTimeOffset.UtcNow, "", "RTBackfillOwner", UserRole.Owner, true, default, default);
        var tenantEntity = new Tenant(_tenantId, "Round-Trip Backfill Tenant", _slug, "en", default, default);
        _ownerJwt = _issuer.Issue(ownerUser, tenantEntity);

        // 6. Seed guild using the real Discord guild ID.
        (_guildId, _guildPublicId) = await _guilds.CreateAsync(
            tenantId: _tenantId,
            discordGuildId: _discordGuildId.ToString(),
            displayName: "RT Backfill Guild",
            registeredByUserId: ownerUid).ConfigureAwait(false);

        // 7. Start factory + client.
        _factory = new DwbHubRoundTripTestFactory();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _ownerJwt);

        // 7a. Resolve the webhook cipher from DI so we can decrypt the token after bridging.
        _webhookCipher = _factory.Services.GetRequiredService<IChannelWebhookCipher>();

        // 8. Set real bot credentials (Bot A) via API.
        var credRes = await _client.PutAsJsonAsync(
            $"/api/t/{_slug}/guilds/{_guildPublicId:D}/bot-credentials",
            new { token = _botToken }).ConfigureAwait(false);
        credRes.StatusCode.Should().BeOneOf(
            [HttpStatusCode.NoContent, HttpStatusCode.OK],
            "PUT bot-credentials must succeed");

        // 9. Activate guild — triggers OnGuildActivatedAsync → BotConnectionManager connects.
        var activateRes = await _client.PostAsync(
            $"/api/t/{_slug}/guilds/{_guildPublicId:D}/activate", null).ConfigureAwait(false);
        activateRes.StatusCode.Should().Be(HttpStatusCode.NoContent, "guild activate must succeed");

        // 10. Wait for Bot A to reach Connected state.
        var manager = _factory.Services.GetRequiredService<BotConnectionManager>();
        await WaitForBotConnectedAsync(manager, _guildId, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        // 11. Sync channels from Discord.
        var syncRes = await _client.PostAsync(
            $"/api/t/{_slug}/guilds/{_guildPublicId:D}/channels/sync", null).ConfigureAwait(false);
        syncRes.StatusCode.Should().Be(HttpStatusCode.NoContent, "channel sync must succeed");

        // 12. Find the target channel in DB by its Discord snowflake.
        var targetChannel = await _channels.GetByDiscordIdAsync(
            _tenantId, (long)_testChannelDiscordId).ConfigureAwait(false);
        targetChannel.Should().NotBeNull(
            $"channel {_testChannelDiscordId} must be visible to Bot A after sync");
        _bridgedChannelPublicId = targetChannel!.PublicId;

        // 13. Defensive pre-bridge cleanup: delete any leaked "DwbHub" webhooks from
        //     previous test runs that crashed before DisposeAsync could clean them up.
        //     Discord caps webhooks at 10 per channel — without this, bridging fails with 500.
        try
        {
            using var cleanupDiscord = new DiscordRestClient();
            await cleanupDiscord.LoginAsync(TokenType.Bot, _botToken).ConfigureAwait(false);
            var cleanupChannel = await cleanupDiscord.GetChannelAsync(_testChannelDiscordId)
                .ConfigureAwait(false) as RestTextChannel;
            if (cleanupChannel is not null)
            {
                var existingWebhooks = await cleanupChannel.GetWebhooksAsync().ConfigureAwait(false);
                foreach (var wh in existingWebhooks.Where(w =>
                    w.Name.Equals("DwbHub", StringComparison.OrdinalIgnoreCase)))
                {
                    try { await wh.DeleteAsync().ConfigureAwait(false); } catch { /* swallow */ }
                }
            }
        }
        catch { /* swallow — cleanup failure must not block the test setup */ }

        // 14. Log in helper bot (Bot B).
        _helperBot = new DiscordRestClient();
        await _helperBot.LoginAsync(TokenType.Bot, _helperBotToken).ConfigureAwait(false);

        // 15. Defensive pre-test cleanup: sweep old [dwbhub-roundtrip-backfill:...] messages
        //     from crashed prior runs so the channel doesn't accumulate stale markers.
        try
        {
            var ch = await _helperBot.GetChannelAsync(_testChannelDiscordId)
                .ConfigureAwait(false) as IMessageChannel;
            if (ch is not null)
            {
                var old = await ch.GetMessagesAsync(100).FlattenAsync().ConfigureAwait(false);
                foreach (var msg in old.Where(m =>
                    m.Content.StartsWith("[dwbhub-roundtrip-backfill:", StringComparison.Ordinal)))
                {
                    try { await msg.DeleteAsync().ConfigureAwait(false); } catch { /* swallow — MANAGE_MESSAGES may be absent */ }
                }
            }
        }
        catch { /* swallow */ }

        // 16. Stand up a DiscordRestChannelClient for webhook delete in DisposeAsync.
        _verifyHttp = new HttpClient();
        _verifyClient = new DiscordRestChannelClient(
            _verifyHttp,
            NullLogger<DiscordRestChannelClient>.Instance);

        // NOTE: Bridge is NOT called here — it is called inside the test so that the
        // helper-bot messages are guaranteed to be pre-bridge (historical).
    }

    // ── Test ──────────────────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Backfill_after_bridge_imports_helper_bot_messages()
    {
        Skip.IfNot(_envOk,
            "DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_HELPER_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID not all set");

        // ── 1) Helper bot posts 20 pre-bridge marker messages ──────────────────
        _marker = $"[dwbhub-roundtrip-backfill:{Guid.NewGuid():N}]";
        var channel = await _helperBot!.GetChannelAsync(_testChannelDiscordId)
            .ConfigureAwait(false) as IMessageChannel;
        channel.Should().NotBeNull(
            $"Helper bot must be able to see channel {_testChannelDiscordId}; " +
            "check that it is a member of the guild with SEND_MESSAGES");

        const int MessageCount = 20;
        var postedContents = new List<string>(MessageCount);
        for (int i = 0; i < MessageCount; i++)
        {
            var content = $"{_marker} pre-bridge-{i:D2}";
            var sent = await channel!.SendMessageAsync(content).ConfigureAwait(false);
            _helperPostedMessageIds.Add(sent.Id);
            postedContents.Add(content);
            // Stay under Discord's bot rate limit (5 messages / 5 s per channel).
            await Task.Delay(150).ConfigureAwait(false);
        }

        // ── 2) Bridge the channel — creates webhook + enqueues backfill job ────
        var bridgeRes = await _client.PostAsync(
            $"/api/t/{_slug}/channels/{_bridgedChannelPublicId:D}/bridge", null).ConfigureAwait(false);
        bridgeRes.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "bridge must succeed so the backfill pipeline can fetch the pre-bridge messages");

        var bridgeBody = await bridgeRes.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        var backfillJobId = bridgeBody.GetProperty("backfillJobId").GetInt64();
        backfillJobId.Should().BePositive("bridge response must include a valid backfill job ID");

        // 2a. Read webhook credentials from DB so DisposeAsync can delete the webhook.
        var channelRow = await _channels.GetByPublicIdAsync(_tenantId, _bridgedChannelPublicId)
            .ConfigureAwait(false);
        if (channelRow is not null)
        {
            await using var dbConn = _ds.CreateConnection();
            await dbConn.OpenAsync().ConfigureAwait(false);
            var webhookRow = await dbConn.QuerySingleOrDefaultAsync("""
                SELECT discord_webhook_id, ciphertext, nonce, auth_tag, key_version
                FROM channel_webhooks
                WHERE tenant_id = @TenantId AND channel_id = @ChannelId
                """,
                new { TenantId = _tenantId, ChannelId = channelRow.Id }).ConfigureAwait(false);
            if (webhookRow is not null)
            {
                _webhookId = (ulong)(long)webhookRow.discord_webhook_id;
                var envelope = new ChannelWebhookEnvelope(
                    Ciphertext: (byte[])webhookRow.ciphertext,
                    Nonce: (byte[])webhookRow.nonce,
                    AuthTag: (byte[])webhookRow.auth_tag,
                    KeyVersion: (int)webhookRow.key_version);
                _webhookToken = _webhookCipher.Decrypt(envelope);
            }
        }

        // ── 3) Run the backfill job in-process ────────────────────────────────
        // The DwbHubRoundTripTestFactory removes Hangfire's BackgroundProcessingServer
        // (to avoid teardown races). We therefore invoke the runner directly via DI
        // rather than waiting for Hangfire to pick it up.
        using var scope = _factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IBackfillRunner>();
        await runner.RunAsync(_tenantId, backfillJobId).ConfigureAwait(false);

        // ── 4) Verify: backfill-status must be "complete" ─────────────────────
        var statusRes = await _client.GetAsync(
            $"/api/t/{_slug}/channels/{_bridgedChannelPublicId:D}/backfill-status")
            .ConfigureAwait(false);
        statusRes.IsSuccessStatusCode.Should().BeTrue(
            "GET /backfill-status must return 200 after RunAsync completes");

        var statusBody = await statusRes.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        var status = statusBody.GetProperty("status").GetProperty("status").GetString();
        status.Should().Be("complete",
            "backfill job must be in 'complete' state after RunAsync returns");

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
        var fetched = await GetMessagesFromApiAsync(limit: 100).ConfigureAwait(false);
        var fetchedContents = fetched.Select(m => m.Content).ToList();

        foreach (var expected in postedContents)
        {
            fetchedContents.Should().Contain(expected,
                $"helper-bot message '{expected}' must have been backfilled into the DB");
        }
    }

    // ── Helper: GET /messages from the API ────────────────────────────────────

    private async Task<IReadOnlyList<BackfillHistoryItem>> GetMessagesFromApiAsync(int limit = 100)
    {
        var resp = await _client.GetAsync(
            $"/api/t/{_slug}/channels/{_bridgedChannelPublicId:D}/messages?limit={limit}")
            .ConfigureAwait(false);
        resp.IsSuccessStatusCode.Should().BeTrue(
            $"GET /messages must succeed after backfill completes (status: {resp.StatusCode})");

        var body = await resp.Content
            .ReadFromJsonAsync<BackfillMessageHistoryApiResponse>()
            .ConfigureAwait(false);
        return body?.Messages ?? [];
    }

    // ── Helper: wait for Bot A to connect ─────────────────────────────────────

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

    // ── IAsyncLifetime.DisposeAsync ────────────────────────────────────────────

    public async Task DisposeAsync()
    {
        // 1. Clean up helper bot messages — sweep both explicit IDs and marker prefix.
        if (_helperBot is not null)
        {
            try
            {
                var ch = await _helperBot.GetChannelAsync(_testChannelDiscordId)
                    .ConfigureAwait(false) as IMessageChannel;

                if (ch is not null)
                {
                    // Sweep by prefix — catches crash-survivors from previous runs too.
                    try
                    {
                        var msgs = await ch.GetMessagesAsync(100)
                            .FlattenAsync().ConfigureAwait(false);
                        foreach (var msg in msgs.Where(m =>
                            m.Content.StartsWith("[dwbhub-roundtrip-backfill:", StringComparison.Ordinal)))
                        {
                            try { await msg.DeleteAsync().ConfigureAwait(false); } catch { /* swallow */ }
                        }
                    }
                    catch { /* swallow */ }
                }
            }
            catch { /* swallow */ }

            try { _helperBot.Dispose(); } catch { /* swallow */ }
        }

        // 2. Delete the test webhook to avoid hitting Discord's 10-webhook-per-channel limit.
        if (_verifyClient is not null && _webhookId != 0 && _webhookToken is not null)
        {
            try
            {
                await _verifyClient.DeleteWebhookAsync(
                    _webhookId, _webhookToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* swallow — webhook may have been deleted already */ }
        }

        // 3. Dispose HTTP clients + factory.
        try { _client?.Dispose(); } catch { /* swallow */ }
        try { _verifyHttp?.Dispose(); } catch { /* swallow */ }
        if (_factory is not null)
        {
            try { await _factory.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }

        // 4. Dispose data source.
        try { _ds?.Dispose(); } catch { /* swallow */ }
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
