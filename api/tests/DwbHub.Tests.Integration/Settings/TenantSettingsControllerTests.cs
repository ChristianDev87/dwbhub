using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

namespace DwbHub.Tests.Integration.Settings;

/// <summary>
/// Integration tests for PATCH /api/t/{slug}/settings.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenantSettingsControllerTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

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
        SqlMapper.AddTypeHandler(new SettingsDateTimeOffsetHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var fac = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(fac);
        _users = new UserRepository(fac);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64JwtKey);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var uniqueLogDir = Path.Combine(Path.GetTempPath(), "dwbhub-settings-test-logs", Guid.NewGuid().ToString("N"));
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
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        _ds.Dispose();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(long tenantId, long userId, string jwt)> SeedUserAsync(
        string slug, string email, UserRole role, string displayName = "TestUser")
    {
        var tid = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: displayName,
            Role: role, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", displayName,
            role, true, default, default);
        var tenant = new Tenant(tid, $"T-{slug}", slug, "en", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    private HttpClient BuildClient(string jwt)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private static string SettingsUrl(string slug) => $"/api/t/{slug}/settings";

    // ── 1. PATCH sets window → 204 + DB persisted ─────────────────────────────

    [Fact]
    public async Task Patch_sets_window_and_returns_204()
    {
        var (tid, _, jwt) = await SeedUserAsync("sets-set", "ss@test.local", UserRole.Owner);

        using var client = BuildClient(jwt);

        var res = await client.PatchAsJsonAsync(SettingsUrl("sets-set"),
            new { messageEditWindowSeconds = 300 });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // DB value must reflect the update.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var windowInDb = await conn.QuerySingleAsync<int?>(
            "SELECT message_edit_window_seconds FROM tenants WHERE id = @tid",
            new { tid });
        windowInDb.Should().Be(300);

        // Audit event must have been written.
        var auditCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE tenant_id = @tid AND event_type = 'tenant.settings.message_edit_window_updated'",
            new { tid });
        auditCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── 2. PATCH with null resets to unlimited ────────────────────────────────

    [Fact]
    public async Task Patch_with_null_resets_to_unlimited()
    {
        var (tid, _, jwt) = await SeedUserAsync("sets-null", "sn@test.local", UserRole.Owner);

        // First set a window.
        await _tenants.UpdateMessageEditWindowAsync(tid, 120, CancellationToken.None);

        using var client = BuildClient(jwt);

        // Now clear it (null = unlimited).
        var res = await client.PatchAsJsonAsync(SettingsUrl("sets-null"),
            new { messageEditWindowSeconds = (int?)null });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var windowInDb = await conn.QuerySingleAsync<int?>(
            "SELECT message_edit_window_seconds FROM tenants WHERE id = @tid",
            new { tid });
        windowInDb.Should().BeNull("null means unlimited — no edit window constraint");
    }

    // ── 3. PATCH out of range → 400 ──────────────────────────────────────────

    [Fact]
    public async Task Patch_out_of_range_returns_400()
    {
        var (_, _, jwt) = await SeedUserAsync("sets-oor", "sor@test.local", UserRole.Owner);

        using var client = BuildClient(jwt);

        // 30 seconds is below the minimum of 60 seconds.
        var res = await client.PatchAsJsonAsync(SettingsUrl("sets-oor"),
            new { messageEditWindowSeconds = 30 });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("out_of_range");
    }

    // ── 4. PATCH as non-Owner → 403 ──────────────────────────────────────────

    [Fact]
    public async Task Patch_non_owner_returns_403()
    {
        // Seed a Member user (Role = Member, not Owner).
        var (_, _, memberJwt) = await SeedUserAsync("sets-forbid", "sfb@test.local", UserRole.Member, "MemberUser");

        using var client = BuildClient(memberJwt);

        var res = await client.PatchAsJsonAsync(SettingsUrl("sets-forbid"),
            new { messageEditWindowSeconds = 300 });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

// ── File-scoped DateTimeOffset handler ───────────────────────────────────────

file sealed class SettingsDateTimeOffsetHandler : Dapper.SqlMapper.TypeHandler<DateTimeOffset>
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
