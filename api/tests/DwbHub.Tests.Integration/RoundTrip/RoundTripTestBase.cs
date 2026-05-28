using System.Net.Http.Headers;
using Dapper;
using Discord.Rest;
using DwbHub.Application.Bot;
using DwbHub.Application.Messaging;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Bot;
using DwbHub.Infrastructure.Messaging;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace DwbHub.Tests.Integration.RoundTrip;

/// <summary>
/// Abstract base for all Discord round-trip test classes.
///
/// Extracts ~70% of shared setup/teardown logic that was duplicated across
/// MessageLifecycleRoundTripTests, InboundMessageLifecycleRoundTripTests,
/// BotReconnectRoundTripTests, and MessageBackfillRoundTripTests.
///
/// Shared lifecycle:
///   InitializeAsync  — reads env, seeds DB, starts factory, sets credentials,
///                      activates guild, syncs channels, pre-cleans webhooks,
///                      bridges channel, reads webhook credentials, logs in verifyClient.
///   DisposeAsync     — deactivates guild, deletes webhook, disposes resources.
///
/// Sub-classes override:
///   TenantSlug       — unique per suite (e.g. "roundtrip-outbound")
///   MarkerPrefix     — used for leftover-artifact cleanup
///   RequiresHelperBot — whether Bot B must be logged in during setup
///   OnSetupCompleted — hook for sub-class-specific post-setup logic
///   OnTeardown       — hook for sub-class-specific pre-teardown logic
/// </summary>
[Collection(DatabaseCollection.Name)]
[Trait("Category", "DiscordRoundTrip")]
public abstract class RoundTripTestBase : IAsyncLifetime
{
    // ── Sub-class contract ────────────────────────────────────────────────────

    /// <summary>Tenant slug, e.g. "roundtrip-outbound". Must be unique per sub-class.</summary>
    protected abstract string TenantSlug { get; }

    /// <summary>
    /// Marker prefix used to identify leftover messages during teardown sweep.
    /// E.g. "[dwbhub-roundtrip-outbound:".
    /// </summary>
    protected abstract string MarkerPrefix { get; }

    /// <summary>
    /// Whether the setup should log in a helper bot (Bot B) and expose it
    /// via <see cref="HelperBot"/>. Set to <c>false</c> for outbound-only tests.
    /// </summary>
    protected abstract bool RequiresHelperBot { get; }

    /// <summary>
    /// Whether the base setup should bridge the target channel.
    /// Set to <c>false</c> for backfill tests that must post pre-bridge messages
    /// before bridging. Default: <c>true</c>.
    /// </summary>
    protected virtual bool BridgeInSetup => true;

    // ── Fixed keys ────────────────────────────────────────────────────────────

    protected const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    protected static readonly string Base64EncKey =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    // ── Infrastructure ────────────────────────────────────────────────────────

    private readonly PostgresFixture _fixture;

    protected DwbHubRoundTripTestFactory Factory = null!;
    protected HttpClient Client = null!;

    // Verification client (reads from Discord, bypasses app stack)
    protected DiscordRestChannelClient VerifyClient = null!;
    private HttpClient _verifyHttp = null!;

    // Helper bot (Bot B) — only set when RequiresHelperBot == true
    protected DiscordRestClient? HelperBot;

    // DB repositories
    protected NpgsqlDataSource Ds = null!;
    private TenantRepository _tenants = null!;
    private UserRepository _users = null!;
    protected GuildRepository Guilds = null!;
    protected GuildChannelRepository Channels = null!;

    // ── Shared test state ─────────────────────────────────────────────────────

    /// <summary>True only when all required env vars were present.</summary>
    protected bool EnvOk { get; private set; }

    protected string? BotToken { get; private set; }
    protected string? HelperBotToken { get; private set; }
    protected ulong DiscordGuildId { get; private set; }
    protected ulong TestChannelDiscordId { get; private set; }

    protected long TenantId { get; private set; }
    protected long GuildId { get; private set; }
    protected Guid GuildPublicId { get; private set; }
    protected Guid BridgedChannelPublicId { get; private set; }
    protected string OwnerJwt { get; private set; } = null!;
    protected JwtIssuer Issuer { get; private set; } = null!;

    protected ulong WebhookId { get; private set; }
    protected string WebhookToken { get; private set; } = null!;
    protected IChannelWebhookCipher WebhookCipher { get; private set; } = null!;

    protected List<ulong> HelperPostedMessageIds { get; } = new();

    // ── Step logging ──────────────────────────────────────────────────────────

    protected ITestOutputHelper Output { get; }

    protected void Log(string step)
    {
        var line = $"[{TenantSlug}] {DateTime.UtcNow:HH:mm:ss.fff} {step}";
        Console.WriteLine(line);   // live in container stdout / GitHub Actions
        Output.WriteLine(line);    // xUnit TRX / test report
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    protected RoundTripTestBase(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Output = output;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
    }

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public virtual async Task InitializeAsync()
    {
        // 1. Read env vars — set EnvOk = false if any required one is missing.
        BotToken = Environment.GetEnvironmentVariable("DISCORD_DEV_BOT_TOKEN");
        HelperBotToken = Environment.GetEnvironmentVariable("DISCORD_DEV_HELPER_BOT_TOKEN");
        var guildIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_GUILD_ID");
        var channelIdStr = Environment.GetEnvironmentVariable("DISCORD_DEV_CHANNEL_ID");

        ulong discordGuildId = 0, testChannelDiscordId = 0;
        var coreEnvOk =
            !string.IsNullOrWhiteSpace(BotToken)
            && !string.IsNullOrWhiteSpace(guildIdStr) && ulong.TryParse(guildIdStr, out discordGuildId)
            && !string.IsNullOrWhiteSpace(channelIdStr) && ulong.TryParse(channelIdStr, out testChannelDiscordId);

        EnvOk = coreEnvOk && (!RequiresHelperBot || !string.IsNullOrWhiteSpace(HelperBotToken));

        if (!EnvOk)
        {
            Log("Setup: env incomplete — test will be skipped");
            return;
        }

        DiscordGuildId = discordGuildId;
        TestChannelDiscordId = testChannelDiscordId;

        Log("Setup: env loaded");

        // 2. Fresh DB.
        await _fixture.ResetAsync().ConfigureAwait(false);

        // 3. Set env vars the app host reads on startup.
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
        Ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var connFactory = new NpgsqlConnectionFactory(Ds);
        _tenants = new TenantRepository(connFactory);
        _users = new UserRepository(connFactory);
        Guilds = new GuildRepository(connFactory);
        Channels = new GuildChannelRepository(connFactory);
        Issuer = new JwtIssuer(Base64JwtKey);

        // 5. Seed: tenant + owner user.
        TenantId = await _tenants.CreateAsync(
            name: $"Round-Trip {TenantSlug} Tenant",
            slug: TenantSlug).ConfigureAwait(false);

        var ownerEmail = $"owner@{TenantSlug}.test";
        var ownerDisplayName = $"RT{TenantSlug.Replace("-", "")}Owner";
        var ownerUid = await _users.CreateAsync(new User(
            Id: 0, TenantId: TenantId,
            Email: ownerEmail,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: new BCryptPasswordHasher().Hash("pw"),
            DisplayName: ownerDisplayName,
            Role: UserRole.Owner,
            IsActive: true,
            CreatedAt: default,
            UpdatedAt: default)).ConfigureAwait(false);
        var ownerUser = new User(ownerUid, TenantId, ownerEmail,
            DateTimeOffset.UtcNow, "", ownerDisplayName, UserRole.Owner, true, default, default);
        var tenantEntity = new Tenant(TenantId, $"Round-Trip {TenantSlug} Tenant", TenantSlug, "en", default, default);
        OwnerJwt = Issuer.Issue(ownerUser, tenantEntity);

        // 6. Seed guild using the real Discord guild ID.
        (GuildId, GuildPublicId) = await Guilds.CreateAsync(
            tenantId: TenantId,
            discordGuildId: DiscordGuildId.ToString(),
            displayName: $"RT {TenantSlug} Guild",
            registeredByUserId: ownerUid).ConfigureAwait(false);

        Log($"Setup: tenant + owner + guild seeded (tenantId={TenantId}, guildId={GuildId})");

        // 7. Start factory + client.
        Factory = new DwbHubRoundTripTestFactory();
        Client = Factory.CreateClient();
        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", OwnerJwt);

        WebhookCipher = Factory.Services.GetRequiredService<IChannelWebhookCipher>();

        // 8. Set real bot credentials via typed API.
        var credRes = await Client.PutBotCredentialsAsync(
            TenantSlug, GuildPublicId,
            new { token = BotToken }).ConfigureAwait(false);
        credRes.StatusCode.Should().BeOneOf(
            [System.Net.HttpStatusCode.NoContent, System.Net.HttpStatusCode.OK],
            "PUT bot-credentials must succeed for the round-trip to work");

        Log("Setup: bot credentials set");

        // 9. Activate guild — triggers OnGuildActivatedAsync → BotConnectionManager connects.
        var activateRes = await Client.ActivateGuildAsync(TenantSlug, GuildPublicId)
            .ConfigureAwait(false);
        activateRes.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent, "guild activate must succeed");

        Log("Setup: bot A activate triggered");

        // 10. Wait for bot to reach Connected state.
        var manager = Factory.Services.GetRequiredService<BotConnectionManager>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await WaitForBotConnectedAsync(manager, GuildId, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        sw.Stop();

        Log($"Setup: bot A connected ({sw.Elapsed.TotalSeconds:F1}s)");

        // 11. Sync channels from Discord.
        var syncRes = await Client.SyncChannelsAsync(TenantSlug, GuildPublicId)
            .ConfigureAwait(false);
        syncRes.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent, "channel sync must succeed");

        Log("Setup: channels synced");

        // 12. Find the target channel in DB by its Discord snowflake.
        var targetChannel = await Channels.GetByDiscordIdAsync(
            TenantId, (long)TestChannelDiscordId).ConfigureAwait(false);
        targetChannel.Should().NotBeNull(
            $"channel {TestChannelDiscordId} must be visible to the bot after sync");
        BridgedChannelPublicId = targetChannel!.PublicId;

        // 13. Defensive pre-bridge cleanup: delete any leaked "DwbHub" webhooks from
        //     previous test runs that crashed before DisposeAsync could clean them up.
        //     Discord caps webhooks at 10 per channel — without this, bridging fails with 500.
        int deletedWebhooks = 0;
        try
        {
            using var cleanupDiscord = new DiscordRestClient();
            await cleanupDiscord.LoginAsync(Discord.TokenType.Bot, BotToken).ConfigureAwait(false);
            var cleanupChannel = await cleanupDiscord.GetChannelAsync(TestChannelDiscordId)
                .ConfigureAwait(false) as Discord.Rest.RestTextChannel;
            if (cleanupChannel is not null)
            {
                var existingWebhooks = await cleanupChannel.GetWebhooksAsync().ConfigureAwait(false);
                foreach (var wh in existingWebhooks.Where(w =>
                    w.Name.Equals("DwbHub", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        await wh.DeleteAsync().ConfigureAwait(false);
                        deletedWebhooks++;
                    }
                    catch { /* swallow */ }
                }
            }
        }
        catch { /* swallow — cleanup failure must not block the test setup */ }

        Log($"Setup: pre-bridge webhook cleanup (deleted {deletedWebhooks})");

        if (BridgeInSetup)
        {
            // 14. Bridge the channel — creates a Discord webhook.
            var bridgeRes = await Client.BridgeChannelAsync(TenantSlug, BridgedChannelPublicId)
                .ConfigureAwait(false);
            bridgeRes.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted,
                "bridge must succeed so the round-trip can proceed");

            // 15. Read webhook credentials from DB for direct-REST edit/delete/cleanup.
            await ReadAndStoreWebhookCredentialsAsync().ConfigureAwait(false);

            Log($"Setup: channel bridged, webhook={WebhookId}");
        }
        else
        {
            Log("Setup: bridge skipped (BridgeInSetup=false — sub-class will bridge in test)");
        }

        // 16. Stand up a DiscordRestChannelClient for verification / cleanup.
        _verifyHttp = new HttpClient();
        VerifyClient = new DiscordRestChannelClient(
            _verifyHttp,
            NullLogger<DiscordRestChannelClient>.Instance);

        // 17. Log in helper bot (Bot B) if required.
        if (RequiresHelperBot)
        {
            HelperBot = new DiscordRestClient();
            await HelperBot.LoginAsync(Discord.TokenType.Bot, HelperBotToken).ConfigureAwait(false);
            Log("Setup: helper bot (Bot B) logged in");
        }

        // 18. Sub-class-specific post-setup hook.
        await OnSetupCompleted().ConfigureAwait(false);

        Log("Setup: complete");
    }

    public virtual async Task DisposeAsync()
    {
        if (!EnvOk)
        {
            // Nothing was set up — nothing to tear down.
            return;
        }

        // Sub-class-specific teardown first (e.g. SignalR disconnect, clock restore).
        await OnTeardown().ConfigureAwait(false);

        Log("Teardown: starting");

        // Marker sweep: remove webhook (outbound) messages posted by this test run.
        if (WebhookId != 0 && WebhookToken is not null && VerifyClient is not null)
        {
            try
            {
                var messages = await VerifyClient.GetMessagesAsync(
                    BotToken!, TestChannelDiscordId, beforeSnowflake: null, limit: 50,
                    CancellationToken.None).ConfigureAwait(false);

                int swept = 0;
                foreach (var msg in messages)
                {
                    if (msg.Content.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                    {
                        try
                        {
                            await VerifyClient.DeleteWebhookMessageAsync(
                                WebhookId, WebhookToken, msg.Id, CancellationToken.None)
                                .ConfigureAwait(false);
                            swept++;
                        }
                        catch { /* swallow */ }
                    }
                }
                Log($"Teardown: marker sweep ({swept} message(s))");
            }
            catch
            {
                Log("Teardown: marker sweep failed (swallowed)");
            }
        }

        // Webhook delete.
        if (WebhookId != 0 && WebhookToken is not null && VerifyClient is not null)
        {
            try
            {
                await VerifyClient.DeleteWebhookAsync(
                    WebhookId, WebhookToken, CancellationToken.None).ConfigureAwait(false);
                Log("Teardown: webhook deleted");
            }
            catch
            {
                Log("Teardown: webhook delete failed (swallowed)");
            }
        }

        // Explicitly deactivate Bot A so it goes offline in Discord immediately.
        if (Client is not null && GuildPublicId != Guid.Empty)
        {
            try
            {
                await Client.DeactivateGuildAsync(TenantSlug, GuildPublicId)
                    .ConfigureAwait(false);
                Log("Teardown: bot A deactivated");
            }
            catch
            {
                Log("Teardown: bot A deactivate failed (swallowed)");
            }
        }

        Client?.Dispose();
        HelperBot?.Dispose();
        _verifyHttp?.Dispose();
        Ds?.Dispose();

        if (Factory is not null)
            await Factory.DisposeAsync().ConfigureAwait(false);

        Log("Teardown: complete");
    }

    // ── Overridable hooks ─────────────────────────────────────────────────────

    /// <summary>
    /// Called after base setup is complete. Sub-classes can set up SignalR clients,
    /// advance clocks, etc. Default: no-op.
    /// </summary>
    protected virtual Task OnSetupCompleted() => Task.CompletedTask;

    /// <summary>
    /// Called before the base teardown runs. Sub-classes clean up their own state
    /// (e.g. stop SignalR, delete helper-bot messages). Default: no-op.
    /// </summary>
    protected virtual Task OnTeardown() => Task.CompletedTask;

    // ── Shared helper methods ─────────────────────────────────────────────────

    /// <summary>
    /// Reads the webhook row from DB and stores the decrypted credentials in
    /// <see cref="WebhookId"/> and <see cref="WebhookToken"/>.
    /// Called from the base setup (step 15) and may also be called from sub-class
    /// test code when <see cref="BridgeInSetup"/> is false.
    /// </summary>
    protected async Task ReadAndStoreWebhookCredentialsAsync()
    {
        var channelRow = await Channels.GetByPublicIdAsync(TenantId, BridgedChannelPublicId)
            .ConfigureAwait(false);
        channelRow.Should().NotBeNull("channel row must exist after bridging");

        if (channelRow is null) return;

        await using var dbConn = Ds.CreateConnection();
        await dbConn.OpenAsync().ConfigureAwait(false);
        var webhookRow = await dbConn.QuerySingleOrDefaultAsync("""
            SELECT discord_webhook_id, ciphertext, nonce, auth_tag, key_version
            FROM channel_webhooks
            WHERE tenant_id = @TenantId AND channel_id = @ChannelId
            """,
            new { TenantId, ChannelId = channelRow.Id }).ConfigureAwait(false);

        if (webhookRow is null) return;

        WebhookId = (ulong)(long)webhookRow.discord_webhook_id;
        var envelope = new ChannelWebhookEnvelope(
            Ciphertext: (byte[])webhookRow.ciphertext,
            Nonce: (byte[])webhookRow.nonce,
            AuthTag: (byte[])webhookRow.auth_tag,
            KeyVersion: (int)webhookRow.key_version);
        WebhookToken = WebhookCipher.Decrypt(envelope);
    }

    /// <summary>
    /// Polls <see cref="BotConnectionManager.GetState"/> until the guild reports
    /// <see cref="BotConnectionState.Connected"/>, or throws on timeout.
    /// </summary>
    protected static async Task WaitForBotConnectedAsync(
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

    /// <summary>
    /// Polls the Discord channel REST endpoint until a message whose content contains
    /// <paramref name="contentSubstring"/> is found, or the timeout elapses.
    /// Returns null on timeout.
    /// </summary>
    protected async Task<DiscordMessageInfo?> PollForDiscordMessageAsync(
        ulong channelId,
        string contentSubstring,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var messages = await VerifyClient.GetMessagesAsync(
                    BotToken!, channelId, beforeSnowflake: null, limit: 50,
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
    protected async Task AssertDiscordMessageGoneAsync(
        ulong channelId,
        ulong messageId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var messages = await VerifyClient.GetMessagesAsync(
                    BotToken!, channelId, beforeSnowflake: null, limit: 50,
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
        true.Should().BeFalse(
            $"Discord message {messageId} was still present in channel {channelId} " +
            $"after {timeout.TotalSeconds:F0}s — delete did not propagate");
    }
}
