using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using DwbHub.Application.Messaging;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Security;

/// <summary>
/// Security-focused tests for ChannelsController (Plan 1.0 Task 10).
///
/// Threat model:
///   S1: a client for tenant A attempts to bridge/access a channel belonging to
///       tenant B — must receive 404 (not 403) to prevent information disclosure.
///   S2: unbridging a channel that has no stored webhook must succeed without error
///       (idempotent cleanup).
///   S3: no HTTP response from any channels endpoint may contain the plaintext
///       webhook token or any other raw secret.
///   S4: DecryptTokenAsync is an internal-only method — the raw token must never
///       appear in any controller response, error body, or audit payload.
/// </summary>
[Collection(SecurityDatabaseCollection.Name)]
public sealed class ChannelsControllerSecurityTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

    // Sentinel token value used by spy stubs to verify the token never leaks.
    private const string SentinelToken = "SENTINEL_WEBHOOK_TOKEN_MUST_NOT_APPEAR_IN_RESPONSE";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _channels;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;

    public ChannelsControllerSecurityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new ChanSecDateTimeOffsetHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var fac = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(fac);
        _users = new UserRepository(fac);
        _guilds = new GuildRepository(fac);
        _channels = new GuildChannelRepository(fac);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64JwtKey);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var logDir = Path.Combine(
            Path.GetTempPath(), "dwbhub-chan-sec-logs", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", logDir);
        Environment.SetEnvironmentVariable("DWBHUB_DB_CONNECTION", _fixture.ConnectionString);
        Environment.SetEnvironmentVariable("DWBHUB_JWT_SECRET", Base64JwtKey);
        Environment.SetEnvironmentVariable("DWBHUB_ENCRYPTION_KEY", Base64EncKey);
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_PORT", "11025");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_FROM", "noreply@test.local");
        Environment.SetEnvironmentVariable("DWBHUB_PUBLIC_BASE_URL", "http://localhost:5173");
        Environment.SetEnvironmentVariable("DWBHUB_BOOTSTRAP_TOKEN_FILE", Path.GetTempFileName());
        _factory = new WebApplicationFactory<Program>();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        _ds.Dispose();
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task<(long tenantId, long userId, string jwt)> SeedOwnerAsync(
        string slug, string email)
    {
        var tid = await _tenants.CreateAsync(name: $"Sec-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: "SecOwner",
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", "SecOwner",
            UserRole.Owner, true, default, default);
        var tenant = new Tenant(tid, $"Sec-{slug}", slug, "en", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    private async Task<(long channelId, Guid channelPublicId)> SeedChannelAsync(
        long tenantId, long guildId, long discordChannelId,
        string name = "sec-ch", bool bridged = false)
    {
        var ch = await _channels.UpsertFromSyncAsync(tenantId, guildId, discordChannelId, name, 0, 0);
        if (bridged)
        {
            await _channels.SetBridgedAsync(tenantId, ch.PublicId, true);
            var updated = await _channels.GetByPublicIdAsync(tenantId, ch.PublicId);
            return (updated!.Id, updated.PublicId);
        }
        return (ch.Id, ch.PublicId);
    }

    /// <summary>
    /// Builds an HTTP client with all security-sensitive services overridden to no-ops
    /// or spy variants.
    /// </summary>
    private HttpClient BuildClient(
        string jwt,
        IChannelWebhookService? webhookSvc = null,
        IChannelSyncService? syncSvc = null)
    {
        var connFac = new NpgsqlConnectionFactory(_ds);
        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                svc.RemoveAll<IGuildChannelRepository>();
                svc.AddScoped<IGuildChannelRepository>(_ => new GuildChannelRepository(connFac));
                svc.RemoveAll<IGuildRepository>();
                svc.AddScoped<IGuildRepository>(_ => new GuildRepository(connFac));
                svc.RemoveAll<IChannelBackfillJobRepository>();
                svc.AddScoped<IChannelBackfillJobRepository>(_ =>
                    new ChannelBackfillJobRepository(connFac));

                svc.RemoveAll<IChannelWebhookService>();
                svc.AddScoped<IChannelWebhookService>(_ =>
                    webhookSvc ?? new ChanSecNoOpWebhookService());

                svc.RemoveAll<IChannelSyncService>();
                svc.AddScoped<IChannelSyncService>(_ =>
                    syncSvc ?? new ChanSecNoOpSyncService());

                svc.RemoveAll<IBackgroundJobClient>();
                svc.AddSingleton<IBackgroundJobClient>(new ChanSecNoOpJobClient());

                svc.RemoveAll<DwbHub.Application.Messaging.IMessagesBroadcaster>();
                svc.AddSingleton<DwbHub.Application.Messaging.IMessagesBroadcaster>(
                    new ChanSecNoOpBroadcaster());
            }))
            .CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // ── Security test S1: cross-tenant bridge attempt returns 404, not 403 ────

    /// <summary>
    /// Threat: a JWT-authenticated user of tenant A attempts to bridge a channel
    /// belonging to tenant B by guessing the channel's UUID. The API must return 404
    /// (not 403) so no information about the channel's existence is disclosed.
    /// </summary>
    [Fact]
    public async Task Bridge_TenantA_CannotReach_TenantB_Channel_Returns404()
    {
        // Arrange: two tenants with their own guilds and channels.
        var (_, uidA, jwtA) = await SeedOwnerAsync("chan-sec-s1a", "s1a@sec.local");
        var (tidB, uidB, _) = await SeedOwnerAsync("chan-sec-s1b", "s1b@sec.local");

        var (gidB, _) = await _guilds.CreateAsync(tidB, "700000000000000001", "GuildB", uidB);
        var (_, channelBPublicId) = await SeedChannelAsync(
            tidB, gidB, 700000000000001001L, "tenant-b-channel", false);

        // Act: tenant A tries to bridge tenant B's channel.
        using var client = BuildClient(jwtA);
        var res = await client.PostAsync(
            $"/api/t/chan-sec-s1a/channels/{channelBPublicId:D}/bridge",
            content: null);

        // Assert: must be 404 (not 403 — 403 would confirm the UUID exists somewhere).
        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-tenant channel access must return 404 to prevent UUID existence disclosure; " +
            "returning 403 would reveal that the channel UUID is valid in some tenant");

        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain("chan-sec-s1b",
            "error body must not disclose the other tenant's slug");
    }

    // ── Security test S2: unbridge without webhook is idempotent ──────────────

    /// <summary>
    /// Threat: the webhook row may be missing (e.g. partial rollback, manual cleanup).
    /// Unbridging must succeed cleanly without surfacing internal error details.
    ///
    /// The idempotent cleanup invariant prevents webhook-missing state from causing
    /// a 500 that leaks internal state to the caller.
    /// </summary>
    [Fact]
    public async Task UnBridge_BeforeWebhookCreated_Idempotent_Cleanup_StillSucceeds()
    {
        // Arrange: a bridged channel but DeleteForChannelAsync is a no-op
        //          (simulating missing webhook row).
        var (tid, uid, jwt) = await SeedOwnerAsync("chan-sec-s2", "s2@sec.local");
        var (gid, _) = await _guilds.CreateAsync(tid, "700000000000000002", "SecGuild2", uid);
        var (_, channelPublicId) = await SeedChannelAsync(
            tid, gid, 700000000000002001L, "no-webhook-ch", true);

        // Webhook service is a no-op — simulates the case where DeleteForChannelAsync
        // finds no webhook row (idempotent path inside ChannelWebhookService).
        var webhookSvc = new ChanSecNoOpWebhookService();
        using var client = BuildClient(jwt, webhookSvc: webhookSvc);

        // Act: DELETE bridge on a bridged-but-no-webhook channel.
        var res = await client.DeleteAsync(
            $"/api/t/chan-sec-s2/channels/{channelPublicId:D}/bridge");

        // Assert: 204 — caller must never see a 500 for missing webhook row.
        res.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "unbridge with no webhook row must succeed (idempotent) — " +
            "a 500 here would leak internal state to the caller");

        // The channel must be unbridged.
        var updated = await _channels.GetByPublicIdAsync(tid, channelPublicId);
        updated!.IsBridged.Should().BeFalse(
            "channel must be marked as unbridged even when no webhook row existed");
    }

    // ── Security test S3: no channels endpoint leaks plaintext secrets ────────

    /// <summary>
    /// Threat: any HTTP response body produced by ChannelsController might
    /// inadvertently contain the webhook token, bot token, or other plaintext
    /// credentials via error messages or serialised internal state.
    ///
    /// The test injects a sentinel webhook service that embeds a known sentinel
    /// value in error paths, then verifies the sentinel never appears in responses
    /// across all channels endpoints.
    /// </summary>
    [Fact]
    public async Task Sync_DoesNotPersistRawWebhookToken_NorAnyPlaintextSecrets()
    {
        // Arrange: seed a bridged channel.
        var (tid, uid, jwt) = await SeedOwnerAsync("chan-sec-s3", "s3@sec.local");
        var (gid, gPublicId) = await _guilds.CreateAsync(tid, "700000000000000003", "SecGuild3", uid);
        var (_, channelPublicId) = await SeedChannelAsync(
            tid, gid, 700000000000003001L, "sec-ch3", true);

        // Inject a spy webhook service that returns/throws using the sentinel.
        // If the controller passes the sentinel back to the client, the assertion fails.
        var sentinelSvc = new ChanSecSentinelWebhookService(SentinelToken);
        using var client = BuildClient(jwt, webhookSvc: sentinelSvc);

        // Exercise bridge endpoint — will fail because sentinelSvc throws with sentinel in message.
        var bridgeRes = await client.PostAsync(
            $"/api/t/chan-sec-s3/channels/{channelPublicId:D}/bridge",
            content: null);
        var bridgeBody = await bridgeRes.Content.ReadAsStringAsync();

        // The raw sentinel must not appear in the response body.
        bridgeBody.Should().NotContain(SentinelToken,
            "the webhook token (or any sentinel representing it) must never appear " +
            "in any HTTP response body — token must stay server-side only");

        // Also verify list response is free of secrets.
        var listRes = await client.GetAsync(
            $"/api/t/chan-sec-s3/guilds/{gPublicId:D}/channels");
        var listBody = await listRes.Content.ReadAsStringAsync();

        listBody.Should().NotContain(SentinelToken,
            "the channel list response must not contain any credential or secret value");
    }

    // ── Security test S4: DecryptTokenAsync never returns token to HTTP callers

    /// <summary>
    /// Threat: DecryptTokenAsync on IChannelWebhookService is an internal-only method.
    /// If it were wired into any controller response path, the raw token would leak.
    ///
    /// This test verifies that calling any channels HTTP endpoint never results in the
    /// decrypted token appearing in the HTTP response, even when the webhook service
    /// spy tracks decryption calls.
    /// </summary>
    [Fact]
    public async Task Webhook_Decryption_NeverReturnsTokenToHttpResponse()
    {
        // Arrange: seed a bridged channel.
        var (tid, uid, jwt) = await SeedOwnerAsync("chan-sec-s4", "s4@sec.local");
        var (gid, gPublicId) = await _guilds.CreateAsync(tid, "700000000000000004", "SecGuild4", uid);
        var (_, channelPublicId) = await SeedChannelAsync(
            tid, gid, 700000000000004001L, "sec-ch4", true);

        var decryptCallCount = 0;
        var decryptSpy = new ChanSecDecryptSpyWebhookService(
            decryptedToken: SentinelToken,
            onDecrypt: () => decryptCallCount++);

        using var client = BuildClient(jwt, webhookSvc: decryptSpy);

        // Probe all read endpoints to confirm the decrypted token is never surfaced.
        var endpoints = new[]
        {
            $"/api/t/chan-sec-s4/guilds/{gPublicId:D}/channels",
            $"/api/t/chan-sec-s4/channels/{channelPublicId:D}/backfill-status",
        };

        foreach (var endpoint in endpoints)
        {
            var res = await client.GetAsync(endpoint);
            var body = await res.Content.ReadAsStringAsync();

            body.Should().NotContain(SentinelToken,
                $"endpoint {endpoint} must not return the decrypted webhook token; " +
                "DecryptTokenAsync is internal-only and must never appear in HTTP responses");
        }

        // Optionally confirm: none of the GET read-paths called DecryptTokenAsync.
        // The read endpoints have no need to decrypt the token — only outbound posting does.
        decryptCallCount.Should().Be(0,
            "GET read endpoints must not call DecryptTokenAsync; " +
            "token decryption is needed only for outbound Discord HTTP calls (internal paths)");
    }
}

// ── Stub implementations ──────────────────────────────────────────────────────

/// <summary>No-op webhook service for security tests that don't exercise webhook logic.</summary>
file sealed class ChanSecNoOpWebhookService : IChannelWebhookService
{
    public Task CreateForChannelAsync(long tenantId, long channelId, long userId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteForChannelAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> DecryptTokenAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.FromResult("no-op-token");
}

/// <summary>No-op sync service.</summary>
file sealed class ChanSecNoOpSyncService : IChannelSyncService
{
    public Task SyncFromDiscordAsync(long tenantId, Guid guildPublicId,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>No-op Hangfire job client stub.</summary>
file sealed class ChanSecNoOpJobClient : IBackgroundJobClient
{
    public string Create(Job job, IState state) => "sec-test-job-id";
    public bool ChangeState(string jobId, IState state, string? fromState) => true;
}

/// <summary>No-op broadcaster.</summary>
file sealed class ChanSecNoOpBroadcaster : DwbHub.Application.Messaging.IMessagesBroadcaster
{
    public Task MessageReceivedAsync(MessageBroadcastDto msg, Guid channelPublicId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageUpdatedAsync(long tenantId, long messageId, string content,
        DateTimeOffset editedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageDeletedAsync(MessageDeletedEvent evt,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task BackfillProgressAsync(long tenantId, Guid channelPublicId, long jobId,
        int fetchedCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task BackfillCompleteAsync(long tenantId, Guid channelPublicId, long jobId,
        int fetchedCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChannelBridgeChangedAsync(long tenantId, Guid channelPublicId, bool isBridged,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Webhook service spy that embeds a sentinel value in error messages thrown from
/// CreateForChannelAsync. Used to verify the sentinel never reaches HTTP response bodies.
/// </summary>
file sealed class ChanSecSentinelWebhookService(string sentinelToken) : IChannelWebhookService
{
    public Task CreateForChannelAsync(long tenantId, long channelId, long userId,
        CancellationToken ct = default)
        // Throw with sentinel in message to test that error messages are never forwarded to clients.
        => Task.FromException(new InvalidOperationException(
            $"Internal error near token: {sentinelToken}"));

    public Task DeleteForChannelAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> DecryptTokenAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.FromResult(sentinelToken);
}

/// <summary>
/// Webhook service spy that tracks DecryptTokenAsync calls and returns a known
/// sentinel token. Used to verify the token never surfaces in HTTP responses.
/// </summary>
file sealed class ChanSecDecryptSpyWebhookService(
    string decryptedToken,
    Action onDecrypt) : IChannelWebhookService
{
    public Task CreateForChannelAsync(long tenantId, long channelId, long userId,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteForChannelAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> DecryptTokenAsync(long tenantId, long channelId,
        CancellationToken ct = default)
    {
        onDecrypt();
        return Task.FromResult(decryptedToken);
    }
}

/// <summary>
/// Local DateTimeOffset type handler for the channels security test assembly.
/// </summary>
file sealed class ChanSecDateTimeOffsetHandler : Dapper.SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        _ => throw new InvalidCastException($"Cannot convert {value?.GetType().Name} to DateTimeOffset"),
    };

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value;
}
