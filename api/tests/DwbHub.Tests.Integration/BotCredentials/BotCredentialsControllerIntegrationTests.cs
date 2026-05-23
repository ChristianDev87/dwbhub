using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.BotCredentials;

[Collection(DatabaseCollection.Name)]
public sealed class BotCredentialsControllerIntegrationTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

    // A realistic-shape Discord bot token (NOT a real token; safe for tests).
    private const string TestToken =
        "TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public BotCredentialsControllerIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _guilds = new GuildRepository(factory);
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
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync().ConfigureAwait(false);
        _ds.Dispose();
    }

    private async Task<(long tenantId, Guid guildPublicId, string ownerJwt, string memberJwt)>
        SeedTenantOwnerMemberAndGuildAsync(string slug = "acme")
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

        var (_, publicId) = await _guilds.CreateAsync(
            tenantId: tid, discordGuildId: "1234567890123456789",
            displayName: "Production", registeredByUserId: ownerUid);

        return (tid, publicId, _issuer.Issue(ownerUser, tenant), _issuer.Issue(memberUser, tenant));
    }

    private static string Url(string slug, Guid guildPublicId) =>
        $"/api/t/{slug}/guilds/{guildPublicId:D}/bot-credentials";

    [Fact]
    public async Task PUT_first_time_returns_204_and_emits_added_audit_event_without_secrets()
    {
        var (tid, pid, owner, _) = await SeedTenantOwnerMemberAndGuildAsync("acme");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        var res = await _client.PutAsJsonAsync(Url("acme", pid), new { token = TestToken });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var (eventType, payload) = await conn.QuerySingleAsync<(string EventType, string Payload)>("""
            SELECT event_type, payload_json::text AS payload
            FROM audit_log WHERE event_type LIKE 'bot_credentials.%'
            ORDER BY id DESC LIMIT 1
        """);
        eventType.Should().Be("bot_credentials.added");

        // SECURITY: audit payload must NOT contain any token material
        payload.Should().NotContain("token", "audit payload must never include token plaintext key");
        payload.Should().NotContain("ciphertext");
        payload.Should().NotContain("nonce");
        payload.Should().NotContain("\"tag\"");
        // But MUST contain the safe fields
        var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("tenantSlug").GetString().Should().Be("acme");
        doc.RootElement.GetProperty("guildPublicId").GetString().Should().Be(pid.ToString("D"));
        _ = tid;
    }

    [Fact]
    public async Task PUT_second_time_returns_204_and_emits_rotated_audit_event()
    {
        var (_, pid, owner, _) = await SeedTenantOwnerMemberAndGuildAsync("acme");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        await _client.PutAsJsonAsync(Url("acme", pid), new { token = TestToken });
        var res = await _client.PutAsJsonAsync(Url("acme", pid), new { token = TestToken });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var eventType = await conn.QuerySingleAsync<string>("""
            SELECT event_type FROM audit_log WHERE event_type LIKE 'bot_credentials.%'
            ORDER BY id DESC LIMIT 1
        """);
        eventType.Should().Be("bot_credentials.rotated");
    }

    [Fact]
    public async Task PUT_as_member_returns_403()
    {
        var (_, pid, _, member) = await SeedTenantOwnerMemberAndGuildAsync("acme");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", member);

        var res = await _client.PutAsJsonAsync(Url("acme", pid), new { token = TestToken });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PUT_with_invalid_token_format_returns_400()
    {
        var (_, pid, owner, _) = await SeedTenantOwnerMemberAndGuildAsync("acme");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        var res = await _client.PutAsJsonAsync(Url("acme", pid), new { token = "bogus.token" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DELETE_configured_returns_204_and_emits_removed_audit()
    {
        var (_, pid, owner, _) = await SeedTenantOwnerMemberAndGuildAsync("acme");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        await _client.PutAsJsonAsync(Url("acme", pid), new { token = TestToken });
        var res = await _client.DeleteAsync(Url("acme", pid));
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var eventType = await conn.QuerySingleAsync<string>("""
            SELECT event_type FROM audit_log WHERE event_type LIKE 'bot_credentials.%'
            ORDER BY id DESC LIMIT 1
        """);
        eventType.Should().Be("bot_credentials.removed");
    }

    [Fact]
    public async Task DELETE_when_not_configured_returns_404()
    {
        var (_, pid, owner, _) = await SeedTenantOwnerMemberAndGuildAsync("acme");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner);

        var res = await _client.DeleteAsync(Url("acme", pid));
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
