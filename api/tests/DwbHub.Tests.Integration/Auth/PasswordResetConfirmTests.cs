using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using DwbHub.Application.Auth;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Email;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Auth;

[Collection(EmailCollection.Name)]
public sealed class PasswordResetConfirmTests : IAsyncLifetime
{
    private const string Base64Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly IPAddress TestIp = IPAddress.Parse("127.0.0.1");

    private readonly PostgresFixture _pg;
    private readonly MailpitFixture _mail;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly AuthTokenRepository _authTokens;
    private readonly RefreshTokenRepository _refreshTokens;
    private readonly RefreshTokenService _refreshService;
    private readonly PasswordResetService _sut;
    private readonly BCryptPasswordHasher _passwordHasher;
    private readonly TokenHasher _tokenHasher;
    private readonly TokenGenerator _tokenGenerator;

    public PasswordResetConfirmTests(PostgresFixture pg, MailpitFixture mail)
    {
        _pg = pg;
        _mail = mail;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_pg.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _authTokens = new AuthTokenRepository(factory);
        _refreshTokens = new RefreshTokenRepository(factory);
        _passwordHasher = new BCryptPasswordHasher();
        _tokenHasher = new TokenHasher();
        _tokenGenerator = new TokenGenerator();
        var jwtIssuer = new JwtIssuer(Base64Key);
        var renderer = new TemplateEmailRenderer();
        var sender = new MailKitEmailSender(_mail.SmtpHost, _mail.SmtpPort, "DwbHub <noreply@test.local>");
        var auditRepo = new DwbHub.Data.Repositories.AuditLogRepository(factory);
        var auditWriter = new DwbHub.Application.Audit.AuditWriter(auditRepo);
        _refreshService = new RefreshTokenService(_refreshTokens, _users, _tokenHasher, _tokenGenerator, jwtIssuer, _tenants, auditWriter);
        _sut = new PasswordResetService(
            _authTokens, _tenants, _users,
            _tokenHasher, _tokenGenerator, _passwordHasher, renderer, sender,
            publicBaseUrl: "http://localhost:5173", auditWriter);
    }

    public async Task InitializeAsync()
    {
        await _pg.ResetAsync();
        await _mail.ResetAsync();
    }
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(Tenant, User, string token)> SeedAndIssueTokenAsync()
    {
        var tenantId = await _tenants.CreateAsync("Acme", "acme");
        var tenant = await _tenants.GetByIdAsync(tenantId) ?? throw new InvalidOperationException();
        var userId = await _users.CreateAsync(new User(
            Id: 0, TenantId: tenantId,
            Email: "alice@acme.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _passwordHasher.Hash("oldpassword"),
            DisplayName: "Alice", Role: UserRole.Member, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = await _users.GetByIdAsync(tenantId, userId) ?? throw new InvalidOperationException();

        await _sut.RequestAsync(tenant.Slug, user.Email, "de", TestIp, null);
        var messages = await _mail.GetMessagesAsync();
        var detail = await _mail.GetMessageDetailAsync(messages[0].ID);
        var token = Uri.UnescapeDataString(Regex.Match(detail.HTML, @"token=([A-Za-z0-9_\-%]+)").Groups[1].Value);
        return (tenant, user, token);
    }

    [Fact]
    public async Task Confirm_returns_Success_revokes_all_refresh_tokens_and_updates_password()
    {
        var (tenant, user, token) = await SeedAndIssueTokenAsync();
        _ = await _refreshService.IssueForLoginAsync(user, tenant, TestIp, "ua-1");
        _ = await _refreshService.IssueForLoginAsync(user, tenant, TestIp, "ua-2");

        var outcome = await _sut.ConfirmAsync(token, "new-strong-pass");

        outcome.Should().BeOfType<ResetConfirmOutcome.Success>();
        var success = (ResetConfirmOutcome.Success)outcome;
        success.SessionsRevoked.Should().Be(2);

        var updated = await _users.GetByIdAsync(tenant.Id, user.Id);
        _passwordHasher.Verify("new-strong-pass", updated!.PasswordHash).Should().BeTrue();

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @uid AND revoked_at IS NULL", conn);
        cmd.Parameters.AddWithValue("uid", user.Id);
        var stillActive = (long)(await cmd.ExecuteScalarAsync())!;
        stillActive.Should().Be(0);
    }

    [Fact]
    public async Task Confirm_returns_Invalid_on_expired_token()
    {
        var (tenant, user, _) = await SeedAndIssueTokenAsync();
        var expiredPlain = _tokenGenerator.GenerateUrlSafeBase64();
        await _authTokens.IssuePasswordResetTokenAsync(
            tenant.Id, user.Email, _tokenHasher.Hash(expiredPlain),
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            TestIp, "ua", default);

        var outcome = await _sut.ConfirmAsync(expiredPlain, "new-strong-pass");

        outcome.Should().BeOfType<ResetConfirmOutcome.Invalid>();
    }

    [Fact]
    public async Task Confirm_returns_WeakPassword_on_short_password()
    {
        var (_, _, token) = await SeedAndIssueTokenAsync();
        var outcome = await _sut.ConfirmAsync(token, "short");
        outcome.Should().BeOfType<ResetConfirmOutcome.WeakPassword>();
    }

    [Fact]
    public async Task Confirm_emits_audit_event_with_confirmed_type()
    {
        var (_, _, token) = await SeedAndIssueTokenAsync();

        await _sut.ConfirmAsync(token, "new-strong-pass");

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var eventType = await conn.QuerySingleAsync<string>(
            "SELECT event_type FROM audit_log WHERE event_type LIKE 'auth.password_reset.%' ORDER BY id DESC LIMIT 1");
        eventType.Should().Be("auth.password_reset.confirmed");
    }
}
