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
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

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
[Collection(DatabaseCollection.Name)]
[Trait("Category", "DiscordRoundTrip")]
public sealed class BotReconnectRoundTripTests : IAsyncLifetime
{
    // ── Marker prefix — unique per run for safe cleanup ────────────────────────
    private static readonly string ReconnectMarkerPrefix =
        $"[dwbhub-roundtrip-reconnect:{Guid.NewGuid():N}]";

    // ── Required env vars ──────────────────────────────────────────────────────
    private string? _botToken;           // Bot A — main app bot
    private string? _helperBotToken;     // Bot B — posts test messages
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
    private string _webhookToken = null!;
    private IChannelWebhookCipher _webhookCipher = null!;

    // ── Verification REST client (direct Discord REST, bypasses app stack) ────
    private HttpClient _verifyHttp = null!;
    private DiscordRestChannelClient _verifyClient = null!;

    // ── Helper bot (Bot B) — Discord REST for posting + cleanup ───────────────
    private DiscordRestClient? _helperBot;
    private readonly List<ulong> _helperPostedMessageIds = new();

    // ── SignalR client ─────────────────────────────────────────────────────────
    private HubConnection? _hubClient;

    public BotReconnectRoundTripTests(PostgresFixture fixture)
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
        _slug = "roundtrip-reconnect";
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
        _tenantId = await _tenants.CreateAsync(name: "Round-Trip Reconnect Tenant", slug: _slug)
            .ConfigureAwait(false);
        var ownerUid = await _users.CreateAsync(new User(
            Id: 0, TenantId: _tenantId,
            Email: "owner@roundtrip-reconnect.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: new BCryptPasswordHasher().Hash("pw"),
            DisplayName: "RTReconnectOwner",
            Role: UserRole.Owner,
            IsActive: true,
            CreatedAt: default,
            UpdatedAt: default)).ConfigureAwait(false);
        var ownerUser = new User(ownerUid, _tenantId, "owner@roundtrip-reconnect.test",
            DateTimeOffset.UtcNow, "", "RTReconnectOwner", UserRole.Owner, true, default, default);
        var tenantEntity = new Tenant(_tenantId, "Round-Trip Reconnect Tenant", _slug, "en", default, default);
        _ownerJwt = _issuer.Issue(ownerUser, tenantEntity);

        // 6. Seed guild using the real Discord guild ID.
        var discordGuildIdStr = _discordGuildId.ToString();
        (_guildId, _guildPublicId) = await _guilds.CreateAsync(
            tenantId: _tenantId,
            discordGuildId: discordGuildIdStr,
            displayName: "RT Reconnect Guild",
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
            var cleanupChannel = await cleanupDiscord.GetChannelAsync(_testChannelDiscordId).ConfigureAwait(false)
                as RestTextChannel;
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

        // 14. Bridge the channel — creates a Discord webhook (gates the inbound persistence path).
        var bridgeRes = await _client.PostAsync(
            $"/api/t/{_slug}/channels/{_bridgedChannelPublicId:D}/bridge", null).ConfigureAwait(false);
        bridgeRes.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "bridge must succeed so the inbound pipeline processes messages after reconnect");

        // 14a. Read webhook credentials from DB so DisposeAsync can delete the webhook.
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

        // 14b. Stand up a DiscordRestChannelClient for webhook delete in DisposeAsync.
        _verifyHttp = new HttpClient();
        _verifyClient = new DiscordRestChannelClient(
            _verifyHttp,
            NullLogger<DiscordRestChannelClient>.Instance);

        // 15. Log in helper bot (Bot B) for posting test messages.
        _helperBot = new DiscordRestClient();
        await _helperBot.LoginAsync(TokenType.Bot, _helperBotToken).ConfigureAwait(false);

        // 16. Build the SignalR client using the in-process test server handler.
        //     LongPolling: same as existing hub tests — avoids Kestrel WebSocket plumbing.
        _hubClient = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "api/hubs/messages"),
                opts =>
                {
                    opts.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    opts.Transports = HttpTransportType.LongPolling;
                    opts.AccessTokenProvider = () => Task.FromResult<string?>(_ownerJwt);
                })
            .Build();
        await _hubClient.StartAsync().ConfigureAwait(false);
        // The hub's OnConnectedAsync adds us to the tenant group automatically via
        // the JWT "tid" claim — no explicit subscribe call required.
    }

    // ── Test: post-reconnect bot receives inbound messages + SignalR + DB ─────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Reconnect_via_api_keeps_bot_functional()
    {
        Skip.IfNot(_envOk, "Discord round-trip env (incl. helper token) not set");

        var manager = _factory.Services.GetRequiredService<BotConnectionManager>();

        // 1) Cool-down bypass: advance the manager's clock by 10 minutes so that if
        //    _lastManualReconnectAt already has an entry (e.g., from a previous test
        //    in the same process), the cool-down check passes immediately.
        //    After the reconnect completes we restore real-time so WaitForBotConnectedAsync
        //    works against actual state transitions.
        var frozenNow = DateTimeOffset.UtcNow.AddMinutes(10);
        manager._now = () => frozenNow;

        // 2) Trigger reconnect via App-API.
        var reconnectRes = await _client.ReconnectBotAsync(_slug, _guildPublicId);
        reconnectRes.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "reconnect endpoint must return 204 — a 429 means the cool-down override did not take effect");

        // Restore real time now that the cooldown gate is past.
        manager._now = () => DateTimeOffset.UtcNow;

        // 3) Wait until BotConnectionState returns to Connected.
        //    OnManualReconnectAsync is fire-and-forget — the HTTP response arrives before
        //    the reconnect is fully established, so we must poll explicitly.
        await WaitForBotConnectedAsync(manager, _guildId, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

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
        var channel = await _helperBot!.GetChannelAsync(_testChannelDiscordId)
            .ConfigureAwait(false) as IMessageChannel;
        channel.Should().NotBeNull(
            $"Helper bot must be able to see channel {_testChannelDiscordId}; " +
            "check that it is a member of the guild with SEND_MESSAGES");

        var sent = await channel!.SendMessageAsync(marker).ConfigureAwait(false);
        _helperPostedMessageIds.Add(sent.Id);

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

        hubPayload.GetProperty("content").GetString().Should().Be(marker,
            "the hub payload content must match the exact text posted by the helper bot");
        hubPayload.GetProperty("viaDwbhub").GetBoolean().Should().BeFalse(
            "an inbound gateway message is NOT sent via DwbHub");
        hubPayload.GetProperty("discordMessageId").GetInt64().Should().Be((long)sent.Id,
            "the Discord snowflake in the hub payload must match the message actually posted");

        // 7) DB persistence path must still work after a reconnect.
        //    Poll with a short window to absorb any lag between broadcast and commit.
        var dbMessage = await PollForDbMessageAsync(marker, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        dbMessage.Should().NotBeNull(
            $"GET /messages must return the inbound message within 10 s of the SignalR event; " +
            $"marker='{marker}'");
        dbMessage!.Content.Should().Be(marker);
        dbMessage.DiscordMessageId.Should().Be((long)sent.Id);
    }

    // ── Helper: poll REST /messages until the marker appears ──────────────────

    private async Task<ReconnectHistoryItem?> PollForDbMessageAsync(string content, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var resp = await _client.GetAsync(
                    $"/api/t/{_slug}/channels/{_bridgedChannelPublicId:D}/messages?limit=20")
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
        // 1. Restore real clock in the manager (defensive — isolated per factory, but clean is clean).
        if (_factory is not null)
        {
            try
            {
                var manager = _factory.Services.GetRequiredService<BotConnectionManager>();
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

        // 3. Clean up helper bot messages — sweep both explicit IDs and marker prefix.
        if (_helperBot is not null)
        {
            try
            {
                var channel = await _helperBot
                    .GetChannelAsync(_testChannelDiscordId)
                    .ConfigureAwait(false) as IMessageChannel;

                if (channel is not null)
                {
                    // Sweep by prefix (catches crash-survivors from previous runs too).
                    try
                    {
                        var msgs = await channel.GetMessagesAsync(50)
                            .FlattenAsync().ConfigureAwait(false);
                        foreach (var msg in msgs.Where(m =>
                            m.Content.StartsWith("[dwbhub-roundtrip-reconnect:", StringComparison.Ordinal)))
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

        // 4. Delete the test webhook to avoid hitting Discord's 10-webhook-per-channel limit.
        if (_verifyClient is not null && _webhookId != 0 && _webhookToken is not null)
        {
            try
            {
                await _verifyClient.DeleteWebhookAsync(
                    _webhookId, _webhookToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* swallow — webhook may have been deleted already */ }
        }

        // 5. Dispose HTTP clients + factory.
        try { _client?.Dispose(); } catch { /* swallow */ }
        try { _verifyHttp?.Dispose(); } catch { /* swallow */ }
        if (_factory is not null)
        {
            try { await _factory.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }

        // 6. Dispose data source.
        try { _ds?.Dispose(); } catch { /* swallow */ }
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
