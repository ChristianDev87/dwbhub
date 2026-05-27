using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using DwbHub.Application.Bot;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Bot;
using DwbHub.Tests.Integration.Bot;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.BotConnection;

[Collection(DatabaseCollection.Name)]
public sealed class BotConnectionLifecycleTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildBotCredentialRepository _credentials;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private DwbHub.Tests.Integration.Bot.FakeBotConnectionFactory _fakeFactory = null!;

    public BotConnectionLifecycleTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var connFactory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(connFactory);
        _users = new UserRepository(connFactory);
        _guilds = new GuildRepository(connFactory);
        _credentials = new GuildBotCredentialRepository(connFactory);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64JwtKey);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
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

        _fakeFactory = new DwbHub.Tests.Integration.Bot.FakeBotConnectionFactory();
        _factory = new DwbHubTestFactory()
            .WithWebHostBuilder(wb => wb.ConfigureTestServices(services =>
            {
                // Replace DiscordNetBotConnectionFactory with DwbHub.Tests.Integration.Bot.FakeBotConnectionFactory.
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IBotConnectionFactory))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddSingleton<IBotConnectionFactory>(_fakeFactory);

                // Replace the real AesGcmBotTokenEncryptor with a fake that always
                // decrypts successfully. This avoids CryptographicException when the
                // manager tries to decrypt the FakeEnvelope bytes stored in the DB.
                var encToRemove = services
                    .Where(d => d.ServiceType == typeof(IBotTokenEncryptor))
                    .ToList();
                foreach (var d in encToRemove) services.Remove(d);
                services.AddSingleton<IBotTokenEncryptor>(new FakeBotTokenEncryptor());
            }));

        _client = _factory.CreateClient();

        // Sync _fakeFactory field to whatever DI actually resolved — they must be the same
        // object because we registered the instance directly, but this makes it explicit.
        var resolvedFactory = _factory.Services
            .GetRequiredService<IBotConnectionFactory>() as DwbHub.Tests.Integration.Bot.FakeBotConnectionFactory;
        if (resolvedFactory is not null)
            _fakeFactory = resolvedFactory;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync().ConfigureAwait(false);
        _ds.Dispose();
    }

    // --- Seed helpers ---

    private static CipherEnvelope FakeEnvelope(byte seed = 0x42) => new(
        Nonce: Enumerable.Repeat(seed, 12).ToArray(),
        Ciphertext: Enumerable.Repeat(seed, 80).ToArray(),
        Tag: Enumerable.Repeat(seed, 16).ToArray());

    /// <summary>
    /// Seeds: tenant + owner user + guild. If hasCredentials=true, inserts credentials.
    /// If isActive=false, deactivates the guild after creation (guilds start active by default).
    /// Returns the tenant, guild internal id, guild public id, owner JWT, and a member JWT.
    /// </summary>
    private async Task<(long tenantId, long guildId, Guid guildPublicId, string ownerJwt, string memberJwt)>
        SeedAsync(string slug, bool isActive = true, bool hasCredentials = false)
    {
        var tid = await _tenants.CreateAsync(name: $"Tenant {slug}", slug: slug);

        var ownerUid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: $"owner@{slug}.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: "Owner", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var ownerUser = new User(ownerUid, tid, $"owner@{slug}.test",
            DateTimeOffset.UtcNow, "", "Owner", UserRole.Owner, true, default, default);

        var memberUid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: $"mem@{slug}.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: "Mem", Role: UserRole.Member, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var memberUser = new User(memberUid, tid, $"mem@{slug}.test",
            DateTimeOffset.UtcNow, "", "Mem", UserRole.Member, true, default, default);

        var tenant = new Tenant(tid, $"T{tid}", slug, "de", default, default);

        var (gid, publicId) = await _guilds.CreateAsync(
            tenantId: tid,
            discordGuildId: "1234567890123456789",
            displayName: "Production",
            registeredByUserId: ownerUid);

        if (hasCredentials)
            await _credentials.UpsertAsync(gid, tid, FakeEnvelope());

        if (!isActive)
            await _guilds.SetActiveAsync(gid, tid, isActive: false);

        return (tid, gid, publicId, _issuer.Issue(ownerUser, tenant), _issuer.Issue(memberUser, tenant));
    }

    private async Task<(string EventType, string PayloadJson)> ReadLatestAuditEventAsync(string eventType)
    {
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<(string EventType, string PayloadJson)>("""
            SELECT event_type, payload_json::text AS payload_json
            FROM audit_log WHERE event_type = @et
            ORDER BY id DESC LIMIT 1
        """, new { et = eventType });
    }

    private async Task<int> CountAuditEventsAsync(string eventType)
    {
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE event_type = @et",
            new { et = eventType });
    }

    // --- Tests ---

    [Fact]
    public async Task Activate_PausedGuild_ReturnsNoContent_AndWritesAudit_AndCallsManager()
    {
        var (_, guildId, publicId, ownerJwt, _) =
            await SeedAsync("acme", isActive: false, hasCredentials: true);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerJwt);

        var res = await _client.ActivateGuildAsync("acme", publicId);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Audit row must exist
        var (evtType, payloadJson) = await ReadLatestAuditEventAsync("guild.activated");
        evtType.Should().Be("guild.activated");

        // Defense-in-depth: payload must not contain any token material
        payloadJson.Should().NotContainAny(
            new[] { "token", "ciphertext", "nonce", "tag" },
            "audit payload must never include credential material");

        // Wait for fire-and-forget connect to complete (bounded poll instead of wall-clock delay).
        var conn = await _fakeFactory.WaitForConnectAsync(guildId, TimeSpan.FromSeconds(5));
        conn.ConnectCallsWithTokens.Should().HaveCount(1,
            "ConnectAsync should have been called exactly once");
    }

    [Fact]
    public async Task Activate_AlreadyActive_ReturnsNoContent_AndDoesNotWriteAudit()
    {
        var (_, _, publicId, ownerJwt, _) =
            await SeedAsync("acme", isActive: true, hasCredentials: false);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerJwt);

        var res = await _client.ActivateGuildAsync("acme", publicId);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // No audit event should have been written (idempotent no-op)
        var count = await CountAuditEventsAsync("guild.activated");
        count.Should().Be(0, "idempotent activate should not write an audit event");

        // The idempotent no-op path must NOT trigger the connection manager either —
        // otherwise we'd redundantly cycle the connection on every duplicate activate.
        _fakeFactory.Created.Should().BeEmpty("idempotent activate should not trigger connection manager");
    }

    [Fact]
    public async Task Deactivate_ActiveGuild_ReturnsNoContent_AndWritesAudit_AndCallsManager()
    {
        var (_, _, publicId, ownerJwt, _) =
            await SeedAsync("acme", isActive: true, hasCredentials: false);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerJwt);

        var res = await _client.DeactivateGuildAsync("acme", publicId);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (evtType, payloadJson) = await ReadLatestAuditEventAsync("guild.deactivated");
        evtType.Should().Be("guild.deactivated");
        payloadJson.Should().NotContainAny(
            new[] { "token", "ciphertext", "nonce", "tag" },
            "audit payload must never include credential material");
    }

    [Fact]
    public async Task Deactivate_AsMember_Returns403()
    {
        var (_, _, publicId, _, memberJwt) =
            await SeedAsync("acme", isActive: true, hasCredentials: false);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberJwt);

        var res = await _client.DeactivateGuildAsync("acme", publicId);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reconnect_ActiveGuild_ReturnsNoContent_AndWritesAudit_AndCallsManager()
    {
        var (_, guildId, publicId, ownerJwt, _) =
            await SeedAsync("acme", isActive: true, hasCredentials: true);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerJwt);

        var res = await _client.ReconnectBotAsync("acme", publicId);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (evtType, payloadJson) = await ReadLatestAuditEventAsync("bot.manual_reconnect");
        evtType.Should().Be("bot.manual_reconnect");
        payloadJson.Should().NotContainAny(
            new[] { "token", "ciphertext", "nonce", "tag" },
            "audit payload must never include credential material");

        // Wait for fire-and-forget reconnect to complete (bounded poll instead of wall-clock delay).
        var conn = await _fakeFactory.WaitForConnectAsync(guildId, TimeSpan.FromSeconds(5));
        conn.ConnectCallsWithTokens.Should().HaveCount(1,
            "ConnectAsync should have been called exactly once");
    }

    [Fact]
    public async Task Reconnect_PausedGuild_Returns400()
    {
        var (_, _, publicId, ownerJwt, _) =
            await SeedAsync("acme", isActive: false, hasCredentials: true);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerJwt);

        var res = await _client.ReconnectBotAsync("acme", publicId);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reconnect_endpoint_returns_429_with_RetryAfter_on_second_call_within_60s()
    {
        var (_, guildId, publicId, ownerJwt, _) =
            await SeedAsync("throttle1", isActive: true, hasCredentials: true);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerJwt);

        // Freeze the manager's clock at a fixed point.
        var t0 = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var manager = _factory.Services.GetRequiredService<BotConnectionManager>();
        manager._now = () => t0;

        // First reconnect — should succeed.
        var first = await _client.ReconnectBotAsync("throttle1", publicId);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var reconnectAuditCount = await CountAuditEventsAsync("bot.manual_reconnect");
        reconnectAuditCount.Should().Be(1);

        // Second reconnect immediately (clock still at t0) — should be throttled.
        var second = await _client.ReconnectBotAsync("throttle1", publicId);
        second.StatusCode.Should().Be((HttpStatusCode)429);

        // Retry-After header must be present and positive.
        second.Headers.Should().ContainKey("Retry-After");
        var retryAfterValue = int.Parse(second.Headers.GetValues("Retry-After").First());
        retryAfterValue.Should().BeGreaterThan(0);

        // Response body must contain error discriminator.
        var body = await second.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be("manual_reconnect_cooldown");
        json.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().Be(retryAfterValue);

        // Both audit events must exist.
        var throttleAuditCount = await CountAuditEventsAsync("bot.manual_reconnect_throttled");
        throttleAuditCount.Should().Be(1, "throttled event must be written to audit log");
    }

    [Fact]
    public async Task Reconnect_endpoint_returns_204_again_after_cooldown_elapses()
    {
        var (_, guildId, publicId, ownerJwt, _) =
            await SeedAsync("throttle2", isActive: true, hasCredentials: true);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerJwt);

        var t0 = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var manager = _factory.Services.GetRequiredService<BotConnectionManager>();
        manager._now = () => t0;

        // First reconnect.
        var first = await _client.ReconnectBotAsync("throttle2", publicId);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Advance clock 61 seconds past the cool-down window.
        manager._now = () => t0.AddSeconds(61);

        // Second reconnect — cool-down expired, should succeed again.
        var second = await _client.ReconnectBotAsync("throttle2", publicId);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "reconnect after 61 s should succeed since the 60 s cool-down has elapsed");
    }
}

