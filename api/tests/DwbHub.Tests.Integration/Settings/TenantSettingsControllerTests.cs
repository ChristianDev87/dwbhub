using System.Net;
using System.Net.Http.Headers;
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

namespace DwbHub.Tests.Integration.Settings;

/// <summary>
/// Integration tests for PATCH /api/t/{slug}/settings.
/// Verifies authorization, validation, persistence, and audit-trail behaviour.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenantSettingsControllerTests : IAsyncLifetime
{
    private const string Base64Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;

    public TenantSettingsControllerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var fac = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(fac);
        _users = new UserRepository(fac);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64Key);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var logDir = Path.Combine(Path.GetTempPath(), "dwbhub-settings-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", logDir);
        Environment.SetEnvironmentVariable("DWBHUB_DB_CONNECTION", _fixture.ConnectionString);
        Environment.SetEnvironmentVariable("DWBHUB_JWT_SECRET", Base64Key);
        Environment.SetEnvironmentVariable("DWBHUB_ENCRYPTION_KEY", Base64Key);
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_PORT", "11025");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_FROM", "noreply@test.local");
        Environment.SetEnvironmentVariable("DWBHUB_PUBLIC_BASE_URL", "http://localhost:5173");
        Environment.SetEnvironmentVariable("DWBHUB_BOOTSTRAP_TOKEN_FILE", Path.GetTempFileName());
        _factory = new DwbHubTestFactory();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        _ds.Dispose();
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task<(long tenantId, long userId, string jwt)> SeedOwnerAsync(string slug)
    {
        var tid = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: $"owner@{slug}.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: "Owner",
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, $"owner@{slug}.test", DateTimeOffset.UtcNow, "", "Owner", UserRole.Owner, true, default, default);
        var tenant = new Tenant(tid, $"T-{slug}", slug, "de", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    private async Task<(long userId, string jwt)> SeedMemberAsync(long tenantId, string slug)
    {
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tenantId, Email: $"member@{slug}.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: "Member",
            Role: UserRole.Member, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tenantId, $"member@{slug}.test", DateTimeOffset.UtcNow, "", "Member", UserRole.Member, true, default, default);
        var tenant = new Tenant(tenantId, $"T-{slug}", slug, "de", default, default);
        return (uid, _issuer.Issue(user, tenant));
    }

    private HttpClient BuildClient(string jwt)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_with_valid_value_returns_204_and_writes_audit()
    {
        var (tid, _, jwt) = await SeedOwnerAsync("settings-valid");
        using var client = BuildClient(jwt);

        var res = await client.PatchTenantSettingsAsync("settings-valid",
            new { messageEditWindowSeconds = 3600 });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify audit entry was written with the correct event type and payload.
        await using var conn = await _ds.OpenConnectionAsync();
        var payload = await conn.QuerySingleOrDefaultAsync<string>(
            """
            SELECT payload_json::text FROM audit_log
            WHERE event_type = 'tenant.settings.message_edit_window_updated'
              AND tenant_id = @Tid
            ORDER BY id DESC LIMIT 1
            """,
            new { Tid = tid });

        payload.Should().NotBeNull();
        payload.Should().Contain("3600");
    }

    [Fact]
    public async Task Patch_with_null_resets_to_default_returns_204()
    {
        var (tid, _, jwt) = await SeedOwnerAsync("settings-null");
        using var client = BuildClient(jwt);

        // First set a custom value.
        await client.PatchTenantSettingsAsync("settings-null",
            new { messageEditWindowSeconds = 7200 });

        // Then reset to default by passing null.
        var res = await client.PatchTenantSettingsAsync("settings-null",
            new { messageEditWindowSeconds = (int?)null });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tenantAfter = await _tenants.GetByIdAsync(tid);
        tenantAfter!.MessageEditWindowSeconds.Should().BeNull(
            "null resets to system default");
    }

    [Fact]
    public async Task Patch_with_out_of_range_returns_400()
    {
        var (_, _, jwt) = await SeedOwnerAsync("settings-oor");
        using var client = BuildClient(jwt);

        // Below minimum (60).
        var res1 = await client.PatchTenantSettingsAsync("settings-oor",
            new { messageEditWindowSeconds = 59 });
        res1.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body1 = await res1.Content.ReadAsStringAsync();
        body1.Should().Contain("out_of_range");

        // Above maximum (31536000).
        var res2 = await client.PatchTenantSettingsAsync("settings-oor",
            new { messageEditWindowSeconds = 31536001 });
        res2.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body2 = await res2.Content.ReadAsStringAsync();
        body2.Should().Contain("out_of_range");
    }

    [Fact]
    public async Task Patch_as_non_owner_returns_403()
    {
        var (tid, _, _) = await SeedOwnerAsync("settings-403");
        var (_, memberJwt) = await SeedMemberAsync(tid, "settings-403");
        using var client = BuildClient(memberJwt);

        var res = await client.PatchTenantSettingsAsync("settings-403",
            new { messageEditWindowSeconds = 3600 });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Patch_persists_value_and_subsequent_GetById_returns_it()
    {
        var (tid, _, jwt) = await SeedOwnerAsync("settings-persist");
        using var client = BuildClient(jwt);

        var res = await client.PatchTenantSettingsAsync("settings-persist",
            new { messageEditWindowSeconds = 900 });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tenant = await _tenants.GetByIdAsync(tid);
        tenant.Should().NotBeNull();
        tenant!.MessageEditWindowSeconds.Should().Be(900,
            "the persisted value must be read back from the DB via GetByIdAsync");
    }
}
