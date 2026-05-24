using System.Net;
using Dapper;
using DwbHub.Application.Auth;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Auth;

[Collection(DatabaseCollection.Name)]
public sealed class RefreshTokenIntegrationTests : IAsyncLifetime
{
    private const string Base64Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly IPAddress TestIp = IPAddress.Parse("127.0.0.1");

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly RefreshTokenRepository _refreshTokens;
    private readonly BCryptPasswordHasher _passwordHasher;
    private readonly TokenHasher _tokenHasher;
    private readonly TokenGenerator _tokenGenerator;
    private readonly JwtIssuer _jwtIssuer;
    private readonly RefreshTokenService _sut;

    public RefreshTokenIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _refreshTokens = new RefreshTokenRepository(factory);
        _passwordHasher = new BCryptPasswordHasher();
        _tokenHasher = new TokenHasher();
        _tokenGenerator = new TokenGenerator();
        _jwtIssuer = new JwtIssuer(Base64Key);
        var auditRepo = new DwbHub.Data.Repositories.AuditLogRepository(factory);
        var auditWriter = new DwbHub.Application.Audit.AuditWriter(auditRepo);
        _sut = new RefreshTokenService(
            _refreshTokens, _users, _tokenHasher, _tokenGenerator, _jwtIssuer, _tenants, auditWriter);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(Tenant tenant, User user)> SeedAsync(
        UserRole role = UserRole.Owner, bool active = true)
    {
        var tenantId = await _tenants.CreateAsync("Acme", "acme");
        var tenant = await _tenants.GetByIdAsync(tenantId) ?? throw new InvalidOperationException();
        var userId = await _users.CreateAsync(new User(
            Id: 0, TenantId: tenantId,
            Email: "alice@acme.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _passwordHasher.Hash("pw"),
            DisplayName: "Alice", Role: role, IsActive: active,
            CreatedAt: default, UpdatedAt: default));
        var user = await _users.GetByIdAsync(tenantId, userId) ?? throw new InvalidOperationException();
        return (tenant, user);
    }

    private async Task<string> IssueTokenAsync(Tenant tenant, User user)
        => await _sut.IssueForLoginAsync(user, tenant, TestIp, "test-ua");

    [Fact]
    public async Task Refresh_returns_Success_and_new_pair_on_valid_token()
    {
        var (tenant, user) = await SeedAsync();
        var plaintext = await IssueTokenAsync(tenant, user);

        var outcome = await _sut.RefreshAsync(plaintext, TestIp, "test-ua");

        outcome.Should().BeOfType<RefreshOutcome.Success>();
        var success = (RefreshOutcome.Success)outcome;
        success.AccessToken.Should().NotBeNullOrEmpty();
        success.RefreshToken.Should().NotBeNullOrEmpty().And.NotBe(plaintext);
    }

    [Fact]
    public async Task Refresh_rotates_old_token_to_revoked_with_replaced_by_pointer()
    {
        var (tenant, user) = await SeedAsync();
        var plaintext = await IssueTokenAsync(tenant, user);

        _ = await _sut.RefreshAsync(plaintext, TestIp, null);

        // The old token row should now have revoked_at != null AND replaced_by_token_id set.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT revoked_at IS NOT NULL, replaced_by_token_id IS NOT NULL " +
            "FROM refresh_tokens WHERE token_hash = @h", conn);
        cmd.Parameters.AddWithValue("h", _tokenHasher.Hash(plaintext));
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        reader.GetBoolean(0).Should().BeTrue();
        reader.GetBoolean(1).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_returns_Invalid_on_unknown_hash()
    {
        await SeedAsync();
        var outcome = await _sut.RefreshAsync("not-a-real-token", TestIp, null);
        outcome.Should().BeOfType<RefreshOutcome.Invalid>();
    }

    [Fact]
    public async Task Refresh_returns_Invalid_on_expired_token()
    {
        var (tenant, user) = await SeedAsync();
        var plaintext = _tokenGenerator.GenerateUrlSafeBase64();
        // Insert directly with expires_at in the past
        await _refreshTokens.InsertAsync(
            tenant.Id, user.Id, _tokenHasher.Hash(plaintext),
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            user.Role, user.IsActive, TestIp, null);

        var outcome = await _sut.RefreshAsync(plaintext, TestIp, null);
        outcome.Should().BeOfType<RefreshOutcome.Invalid>();
    }

    [Fact]
    public async Task Refresh_returns_RightsChanged_when_user_role_changed()
    {
        var (tenant, user) = await SeedAsync(role: UserRole.Admin);
        var plaintext = await IssueTokenAsync(tenant, user);

        // Mutate role server-side
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE users SET role = 'Member' WHERE id = @id AND tenant_id = @t", conn);
            cmd.Parameters.AddWithValue("id", user.Id);
            cmd.Parameters.AddWithValue("t", tenant.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        var outcome = await _sut.RefreshAsync(plaintext, TestIp, null);
        outcome.Should().BeOfType<RefreshOutcome.RightsChanged>();
    }

    [Fact]
    public async Task Refresh_returns_RightsChanged_when_user_deactivated()
    {
        var (tenant, user) = await SeedAsync();
        var plaintext = await IssueTokenAsync(tenant, user);

        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE users SET is_active = false WHERE id = @id AND tenant_id = @t", conn);
            cmd.Parameters.AddWithValue("id", user.Id);
            cmd.Parameters.AddWithValue("t", tenant.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        var outcome = await _sut.RefreshAsync(plaintext, TestIp, null);
        outcome.Should().BeOfType<RefreshOutcome.RightsChanged>();
    }

    [Fact]
    public async Task Refresh_returns_ChainCompromised_on_reuse_of_revoked_token()
    {
        var (tenant, user) = await SeedAsync();
        var plaintext = await IssueTokenAsync(tenant, user);

        // First refresh — succeeds, rotates the token.
        _ = await _sut.RefreshAsync(plaintext, TestIp, null);

        // Second refresh of the SAME (now-revoked) plaintext — chain compromise.
        var outcome = await _sut.RefreshAsync(plaintext, TestIp, null);
        outcome.Should().BeOfType<RefreshOutcome.ChainCompromised>();
    }

    [Fact]
    public async Task Refresh_revokes_whole_forward_chain_when_compromise_detected()
    {
        var (tenant, user) = await SeedAsync();
        var t1 = await IssueTokenAsync(tenant, user);
        var firstOutcome = (RefreshOutcome.Success)await _sut.RefreshAsync(t1, TestIp, null);
        var t2 = firstOutcome.RefreshToken;
        var secondOutcome = (RefreshOutcome.Success)await _sut.RefreshAsync(t2, TestIp, null);
        var t3 = secondOutcome.RefreshToken;

        // Now reuse t1 → triggers chain revocation.
        _ = await _sut.RefreshAsync(t1, TestIp, null);

        // t2 and t3 should both be revoked now.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM refresh_tokens WHERE revoked_at IS NULL", conn);
        var unrevokedCount = (long)(await cmd.ExecuteScalarAsync())!;
        unrevokedCount.Should().Be(0);
    }

    /// <summary>
    /// Spec §2.3 reuse-detection invariant: re-presenting a consumed token must both
    /// fail AND revoke the entire forward chain.  This test asserts the service-level
    /// outcome when the successor token (tokenB) is presented AFTER the chain was
    /// revoked by a replay of the predecessor (tokenA).
    ///
    /// Attack sequence reproduced:
    ///   1. Issue tokenA (legitimate login).
    ///   2. refresh(tokenA) → succeeds, produces tokenB.  tokenA is now consumed.
    ///   3. refresh(tokenA) again → ChainCompromised; service marks the whole family revoked.
    ///   4. refresh(tokenB) → must also fail because the chain revocation in step 3
    ///      set revoked_at on tokenB.
    /// </summary>
    [Fact]
    public async Task Refresh_reuse_of_tokenA_invalidates_tokenB_which_also_fails()
    {
        var (tenant, user) = await SeedAsync();

        // Step 1: issue initial token (tokenA).
        var tokenA = await IssueTokenAsync(tenant, user);

        // Step 2: first rotation — legitimate use; succeeds, produces tokenB.
        var firstOutcome = await _sut.RefreshAsync(tokenA, TestIp, "test-ua");
        firstOutcome.Should().BeOfType<RefreshOutcome.Success>("first use of tokenA must succeed");
        var tokenB = ((RefreshOutcome.Success)firstOutcome).RefreshToken;

        // Step 3: replay tokenA — must fail with ChainCompromised and revoke the family.
        var replayOutcome = await _sut.RefreshAsync(tokenA, TestIp, "attacker-ua");
        replayOutcome.Should().BeOfType<RefreshOutcome.ChainCompromised>(
            "replaying a consumed token signals token theft; the whole family must be revoked");

        // Step 4: tokenB was part of the revoked chain — presenting it must also fail.
        var tokenBOutcome = await _sut.RefreshAsync(tokenB, TestIp, "test-ua");
        tokenBOutcome.Should().BeOfType<RefreshOutcome.ChainCompromised>(
            "tokenB was revoked as part of the family; the legitimate holder must be forced to re-login");
    }

    [Fact]
    public async Task Logout_revokes_current_token_returns_no_throw()
    {
        var (tenant, user) = await SeedAsync();
        var plaintext = await IssueTokenAsync(tenant, user);

        await _sut.LogoutAsync(plaintext);

        // Token now revoked; refresh should fail Invalid (revoked_at set means token_found+was_revoked → ChainCompromised).
        // Logout idempotently sets revoked_at; subsequent refresh trips theft-detection branch.
        var outcome = await _sut.RefreshAsync(plaintext, TestIp, null);
        outcome.Should().BeOfType<RefreshOutcome.ChainCompromised>();
    }

    [Fact]
    public async Task Logout_is_idempotent_on_unknown_token()
    {
        await SeedAsync();
        // Should not throw
        await _sut.LogoutAsync("not-a-real-token");
    }

    [Fact]
    public async Task IssueForLoginAsync_records_snapshot_of_current_state()
    {
        var (tenant, user) = await SeedAsync(role: UserRole.Moderator);

        var plaintext = await _sut.IssueForLoginAsync(user, tenant, TestIp, "ua");

        // Verify the snapshot in DB
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT issued_role, issued_was_active FROM refresh_tokens WHERE token_hash = @h", conn);
        cmd.Parameters.AddWithValue("h", _tokenHasher.Hash(plaintext));
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        reader.GetString(0).Should().Be("Moderator");
        reader.GetBoolean(1).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_success_token_can_be_refreshed_again_in_chain()
    {
        var (tenant, user) = await SeedAsync();
        var t1 = await IssueTokenAsync(tenant, user);
        var s1 = (RefreshOutcome.Success)await _sut.RefreshAsync(t1, TestIp, null);
        var s2 = await _sut.RefreshAsync(s1.RefreshToken, TestIp, null);
        s2.Should().BeOfType<RefreshOutcome.Success>();
    }

    [Fact]
    public async Task Refresh_rotation_emits_rotated_event()
    {
        var (tenant, user) = await SeedAsync();
        var plaintext = await IssueTokenAsync(tenant, user);
        await _sut.RefreshAsync(plaintext, TestIp, "ua-test");

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var et = await conn.QuerySingleAsync<string>(
            "SELECT event_type FROM audit_log WHERE event_type LIKE 'auth.refresh.%' ORDER BY id DESC LIMIT 1");
        et.Should().Be("auth.refresh.rotated");
    }
}
