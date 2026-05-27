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
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

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
[Collection(DatabaseCollection.Name)]
[Trait("Category", "DiscordRoundTrip")]
public sealed class InboundMessageLifecycleRoundTripTests : IAsyncLifetime
{
    // ── Marker prefix — unique per run for safe cleanup ────────────────────────
    private static readonly string InboundMarkerPrefix =
        $"[dwbhub-roundtrip-inbound:{Guid.NewGuid():N}]";

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
    private string _tenantSlug = null!;
    private JwtIssuer _issuer = null!;
    private string _ownerJwt = null!;
    private Guid _bridgedChannelPublicId;
    private long _tenantId;
    private long _guildId;

    // ── Webhook credentials (recovered from DB after bridge) ──────────────────
    private ulong _webhookId;
    private string _webhookToken = null!;
    private IChannelWebhookCipher _webhookCipher = null!;

    // ── Verification REST client (direct Discord REST, bypasses app stack) ────
    private HttpClient _verifyHttp = null!;
    private DiscordRestChannelClient _verifyClient = null!;

    // ── Helper bot (Bot B) — Discord REST for posting + cleanup ───────────────
    private DiscordRestClient? _helperRestClient;
    private readonly List<ulong> _helperPostedMessageIds = new();

    // ── SignalR client ─────────────────────────────────────────────────────────
    private HubConnection? _hubClient;

    public InboundMessageLifecycleRoundTripTests(PostgresFixture fixture)
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
        _tenantSlug = "roundtrip-inbound";
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
        _tenantId = await _tenants.CreateAsync(name: "Round-Trip Inbound Tenant", slug: _tenantSlug)
            .ConfigureAwait(false);
        var ownerUid = await _users.CreateAsync(new User(
            Id: 0, TenantId: _tenantId,
            Email: "owner@roundtrip-inbound.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: new BCryptPasswordHasher().Hash("pw"),
            DisplayName: "RTInboundOwner",
            Role: UserRole.Owner,
            IsActive: true,
            CreatedAt: default,
            UpdatedAt: default)).ConfigureAwait(false);
        var ownerUser = new User(ownerUid, _tenantId, "owner@roundtrip-inbound.test",
            DateTimeOffset.UtcNow, "", "RTInboundOwner", UserRole.Owner, true, default, default);
        var tenantEntity = new Tenant(_tenantId, "Round-Trip Inbound Tenant", _tenantSlug, "en", default, default);
        _ownerJwt = _issuer.Issue(ownerUser, tenantEntity);

        // 6. Seed guild using the real Discord guild ID.
        var discordGuildIdStr = _discordGuildId.ToString();
        var (guildId, guildPublicId) = await _guilds.CreateAsync(
            tenantId: _tenantId,
            discordGuildId: discordGuildIdStr,
            displayName: "RT Inbound Guild",
            registeredByUserId: ownerUid).ConfigureAwait(false);
        _guildId = guildId;

        // 7. Start factory + client.
        _factory = new DwbHubRoundTripTestFactory();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _ownerJwt);

        // 7a. Resolve the webhook cipher from DI so we can decrypt the token after bridging.
        _webhookCipher = _factory.Services.GetRequiredService<IChannelWebhookCipher>();

        // 8. Set real bot credentials (Bot A) via API.
        var credRes = await _client.PutAsJsonAsync(
            $"/api/t/{_tenantSlug}/guilds/{guildPublicId:D}/bot-credentials",
            new { token = _botToken }).ConfigureAwait(false);
        credRes.StatusCode.Should().BeOneOf(
            [HttpStatusCode.NoContent, HttpStatusCode.OK],
            "PUT bot-credentials must succeed");

        // 9. Activate guild — triggers OnGuildActivatedAsync → BotConnectionManager connects.
        var activateRes = await _client.PostAsync(
            $"/api/t/{_tenantSlug}/guilds/{guildPublicId:D}/activate", null).ConfigureAwait(false);
        activateRes.StatusCode.Should().Be(HttpStatusCode.NoContent, "guild activate must succeed");

        // 10. Wait for Bot A to reach Connected state.
        var manager = _factory.Services.GetRequiredService<BotConnectionManager>();
        await WaitForBotConnectedAsync(manager, guildId, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        // 11. Sync channels from Discord.
        var syncRes = await _client.PostAsync(
            $"/api/t/{_tenantSlug}/guilds/{guildPublicId:D}/channels/sync", null).ConfigureAwait(false);
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

        // 14. Bridge the channel — creates a Discord webhook (needed for is_bridged=true,
        //     which gates the inbound persistence path in MessageService.PersistInboundAsync).
        var bridgeRes = await _client.PostAsync(
            $"/api/t/{_tenantSlug}/channels/{_bridgedChannelPublicId:D}/bridge", null).ConfigureAwait(false);
        bridgeRes.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "bridge must succeed so the inbound pipeline processes messages for this channel");

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
        _helperRestClient = new DiscordRestClient();
        await _helperRestClient.LoginAsync(TokenType.Bot, _helperBotToken).ConfigureAwait(false);

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

    // ── Test: inbound message from helper bot arrives via SignalR + DB ─────────

    [SkippableFact]
    [Trait("Category", "DiscordRoundTrip")]
    public async Task Discord_message_via_helper_bot_arrives_via_signalr_and_in_db()
    {
        Skip.IfNot(_envOk,
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
        var channel = await _helperRestClient!.GetChannelAsync(_testChannelDiscordId)
            .ConfigureAwait(false) as IMessageChannel;
        channel.Should().NotBeNull(
            $"Helper bot must be able to see channel {_testChannelDiscordId}; " +
            "check that it is a member of the guild with READ_MESSAGES");

        var sentMessage = await channel!.SendMessageAsync(marker).ConfigureAwait(false);
        _helperPostedMessageIds.Add(sentMessage.Id);

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

        // ── Assertions: SignalR payload ────────────────────────────────────────
        received.Content.Should().Be(marker,
            "the hub payload content must match the exact text posted by the helper bot");
        received.ViaDwbhub.Should().BeFalse(
            "an inbound gateway message is NOT sent via DwbHub");
        received.DiscordMessageId.Should().Be((long)sentMessage.Id,
            "the Discord snowflake in the hub payload must match the message actually posted");

        // ── DB verification: the message must be persisted ─────────────────────
        // Poll with a short window to absorb any lag between broadcast and commit.
        var dbMessage = await PollForDbMessageAsync(marker, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        dbMessage.Should().NotBeNull(
            $"GET /messages must return the inbound message within 10s of the SignalR event; " +
            $"marker='{marker}'");
        dbMessage!.Content.Should().Be(marker);
        dbMessage.DiscordMessageId.Should().Be((long)sentMessage.Id);
    }

    // ── Helper: poll REST /messages until the marker appears ──────────────────

    private async Task<HistoryItem?> PollForDbMessageAsync(string content, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var resp = await _client.GetAsync(
                    $"/api/t/{_tenantSlug}/channels/{_bridgedChannelPublicId:D}/messages?limit=20")
                    .ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content
                        .ReadFromJsonAsync<MessageHistoryApiResponse>()
                        .ConfigureAwait(false);
                    var match = body?.Messages?.FirstOrDefault(m =>
                        m.Content == content);
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
        // 1. Stop SignalR client gracefully.
        if (_hubClient is not null)
        {
            try { await _hubClient.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }
            try { await _hubClient.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }

        // 2. Clean up helper bot messages — sweep both explicit IDs and marker prefix.
        if (_helperRestClient is not null)
        {
            try
            {
                var channel = await _helperRestClient
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
                            m.Content.StartsWith("[dwbhub-roundtrip-inbound:", StringComparison.Ordinal)))
                        {
                            try { await msg.DeleteAsync().ConfigureAwait(false); } catch { /* swallow */ }
                        }
                    }
                    catch { /* swallow */ }
                }
            }
            catch { /* swallow */ }

            try { _helperRestClient.Dispose(); } catch { /* swallow */ }
        }

        // 3. Delete the test webhook to avoid hitting Discord's 10-webhook-per-channel limit.
        if (_verifyClient is not null && _webhookId != 0 && _webhookToken is not null)
        {
            try
            {
                await _verifyClient.DeleteWebhookAsync(
                    _webhookId, _webhookToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* swallow — webhook may have been deleted already */ }
        }

        // 4. Dispose HTTP client + factory.
        try { _client?.Dispose(); } catch { /* swallow */ }
        try { _verifyHttp?.Dispose(); } catch { /* swallow */ }
        if (_factory is not null)
        {
            try { await _factory.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }

        // 5. Dispose data source.
        try { _ds?.Dispose(); } catch { /* swallow */ }
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
