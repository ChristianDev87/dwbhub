using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Tenancy;

[Collection(DatabaseCollection.Name)]
public sealed class TenantRoutingIntegrationTests : IAsyncLifetime
{
    private const string Base64Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;

    // Initialized in InitializeAsync to avoid parallel-constructor log-file races.
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public TenantRoutingIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64Key);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();

        // Program.cs reads environment variables directly (not IConfiguration).
        // Set them just before creating the factory so each test gets a fresh host
        // pointed at the right DB. A unique log directory avoids Serilog file-lock
        // collisions when factories are spun up in rapid succession.
        var uniqueLogDir = Path.Combine(
            Path.GetTempPath(), "dwbhub-test-logs", Guid.NewGuid().ToString("N"));

        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", uniqueLogDir);
        Environment.SetEnvironmentVariable("DWBHUB_DB_CONNECTION", _fixture.ConnectionString);
        Environment.SetEnvironmentVariable("DWBHUB_JWT_SECRET", Base64Key);
        Environment.SetEnvironmentVariable("DWBHUB_ENCRYPTION_KEY", Base64Key);
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
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }
        _ds.Dispose();
    }

    private async Task<(long tenantId, long userId)> SeedTenantAndUserAsync(
        string slug, string email, UserRole role = UserRole.Owner)
    {
        var tenantId = await _tenants.CreateAsync(name: $"Tenant {slug}", slug: slug);
        var userId = await _users.CreateAsync(new User(
            Id: 0,
            TenantId: tenantId,
            Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: $"User {email}",
            Role: role,
            IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        return (tenantId, userId);
    }

    private string IssueJwtFor(long userId, long tenantId, string slug, UserRole role = UserRole.Owner)
    {
        var user = new User(
            Id: userId, TenantId: tenantId, Email: "x@y.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow, PasswordHash: "",
            DisplayName: "X", Role: role, IsActive: true,
            CreatedAt: default, UpdatedAt: default);
        var tenant = new Tenant(
            Id: tenantId, Name: $"T{tenantId}", Slug: slug, Locale: "de",
            CreatedAt: default, UpdatedAt: default);
        return _issuer.Issue(user, tenant);
    }

    [Fact]
    public async Task GET_me_with_matching_jwt_returns_200_with_tenant_data()
    {
        var (tid, uid) = await SeedTenantAndUserAsync("acme", "alice@acme.test");
        var jwt = IssueJwtFor(uid, tid, "acme");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var res = await _client.GetMeAsync("acme");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<MeShape>();
        body!.userId.Should().Be(uid);
        body.tenantId.Should().Be(tid);
        body.tenantSlug.Should().Be("acme");
    }

    [Fact]
    public async Task GET_dashboard_with_matching_jwt_returns_tenant_info()
    {
        var (tid, uid) = await SeedTenantAndUserAsync("acme", "alice@acme.test");
        var jwt = IssueJwtFor(uid, tid, "acme");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var res = await _client.GetDashboardAsync("acme");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<DashboardShape>();
        body!.tenantId.Should().Be(tid);
        body.tenantSlug.Should().Be("acme");
        body.tenantName.Should().Be("Tenant acme");
        body.locale.Should().Be("de");
    }

    [Fact]
    public async Task GET_dashboard_cross_tenant_returns_403_and_audit_row()
    {
        var (tidA, uidA) = await SeedTenantAndUserAsync("acme", "alice@acme.test");
        var (tidB, _) = await SeedTenantAndUserAsync("globex", "bob@globex.test");

        var jwt = IssueJwtFor(uidA, tidA, "acme");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var res = await _client.GetDashboardAsync("globex");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("cross_tenant_access_denied");

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var (eventType, payloadJwtTid) = await conn.QuerySingleAsync<(string EventType, long JwtTid)>(
            """
            SELECT event_type,
                   (payload_json->>'jwtTenantId')::bigint AS jwt_tid
            FROM audit_log
            WHERE event_type = 'auth.cross_tenant_access_blocked'
            ORDER BY id DESC LIMIT 1
            """);
        eventType.Should().Be("auth.cross_tenant_access_blocked");
        payloadJwtTid.Should().Be(tidA);

        _ = tidB; // seeded to verify the middleware picks the right tenant
    }

    [Fact]
    public async Task GET_dashboard_unknown_tenant_returns_404_and_audit_row()
    {
        var res = await _client.GetDashboardAsync("nonexistent");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var eventType = await conn.QuerySingleAsync<string>(
            """
            SELECT event_type FROM audit_log
            WHERE event_type = 'auth.unknown_tenant_access'
            ORDER BY id DESC LIMIT 1
            """);
        eventType.Should().Be("auth.unknown_tenant_access");
    }

    [Fact]
    public async Task GET_dashboard_without_jwt_returns_401()
    {
        await SeedTenantAndUserAsync("acme", "alice@acme.test");
        var res = await _client.GetDashboardAsync("acme");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_health_bypasses_tenant_middleware()
    {
        var res = await _client.GetHealthAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var count = await conn.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM audit_log WHERE event_type LIKE 'auth.%_tenant_access%'");
        count.Should().Be(0);
    }

    [Fact]
    public async Task GET_setup_status_bypasses_tenant_middleware()
    {
        var res = await _client.GetSetupStatusAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_auth_refresh_bypasses_tenant_middleware()
    {
        var res = await _client.PostAuthRefreshAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_guild_scoped_route_with_unknown_uuid_returns_404_and_audit_row()
    {
        await SeedTenantAndUserAsync("acme", "alice@acme.test");
        var pid = Guid.NewGuid();
        var res = await _client.GetGuildAnythingAsync("acme", pid);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await res.Content.ReadAsStringAsync()).Should().Contain("guild_not_found");

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var eventType = await conn.QuerySingleAsync<string>("""
            SELECT event_type FROM audit_log
            WHERE event_type = 'guild.unknown_access'
            ORDER BY id DESC LIMIT 1
        """);
        eventType.Should().Be("guild.unknown_access");
    }

    [Fact]
    public async Task GET_guild_scoped_route_with_unknown_tenant_returns_404_with_tenant_audit()
    {
        var pid = Guid.NewGuid();
        var res = await _client.GetGuildAnythingAsync("nonexistent", pid);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await res.Content.ReadAsStringAsync()).Should().Contain("tenant_not_found");

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var eventType = await conn.QuerySingleAsync<string>("""
            SELECT event_type FROM audit_log
            WHERE event_type = 'auth.unknown_tenant_access'
            ORDER BY id DESC LIMIT 1
        """);
        eventType.Should().Be("auth.unknown_tenant_access");
    }

    private sealed record MeShape(long userId, long tenantId, string tenantSlug, string role);
    private sealed record DashboardShape(long tenantId, string tenantSlug, string tenantName, string locale);
}
