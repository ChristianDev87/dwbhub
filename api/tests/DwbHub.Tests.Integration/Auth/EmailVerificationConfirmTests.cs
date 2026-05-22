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
public sealed class EmailVerificationConfirmTests : IAsyncLifetime
{
    private static readonly IPAddress TestIp = IPAddress.Parse("127.0.0.1");

    private readonly PostgresFixture _pg;
    private readonly MailpitFixture _mail;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly AuthTokenRepository _authTokens;
    private readonly BCryptPasswordHasher _passwordHasher;
    private readonly TokenHasher _tokenHasher;
    private readonly TokenGenerator _tokenGenerator;
    private readonly EmailVerificationService _sut;

    public EmailVerificationConfirmTests(PostgresFixture pg, MailpitFixture mail)
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
        _passwordHasher = new BCryptPasswordHasher();
        _tokenHasher = new TokenHasher();
        _tokenGenerator = new TokenGenerator();
        var renderer = new TemplateEmailRenderer();
        var sender = new MailKitEmailSender(_mail.SmtpHost, _mail.SmtpPort, "DwbHub <noreply@test.local>");
        _sut = new EmailVerificationService(
            _authTokens, _tenants, _users, _tokenHasher, _tokenGenerator, renderer, sender,
            publicBaseUrl: "http://localhost:5173");
    }

    public async Task InitializeAsync()
    {
        await _pg.ResetAsync();
        await _mail.ResetAsync();
    }
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(Tenant, User)> SeedUnverifiedAsync()
    {
        var tenantId = await _tenants.CreateAsync("Acme", "acme");
        var tenant = await _tenants.GetByIdAsync(tenantId) ?? throw new InvalidOperationException();
        var userId = await _users.CreateAsync(new User(
            Id: 0, TenantId: tenantId,
            Email: "alice@acme.test",
            EmailVerifiedAt: null,
            PasswordHash: _passwordHasher.Hash("pw12345678"),
            DisplayName: "Alice", Role: UserRole.Member, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = await _users.GetByIdAsync(tenantId, userId) ?? throw new InvalidOperationException();
        return (tenant, user);
    }

    private async Task<string> IssueAndExtractTokenAsync(Tenant tenant, User user)
    {
        await _sut.ResendAsync(tenant.Slug, user.Email, "de", TestIp, "test-ua");
        var messages = await _mail.GetMessagesAsync();
        messages.Should().HaveCount(1);
        var detail = await _mail.GetMessageDetailAsync(messages[0].ID);
        var match = Regex.Match(detail.HTML, @"token=([A-Za-z0-9_\-%]+)");
        match.Success.Should().BeTrue("token must appear in the rendered email");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [Fact]
    public async Task Confirm_returns_Success_and_sets_email_verified_at()
    {
        var (tenant, user) = await SeedUnverifiedAsync();
        var token = await IssueAndExtractTokenAsync(tenant, user);

        var outcome = await _sut.ConfirmAsync(token);

        outcome.Should().BeOfType<VerifyConfirmOutcome.Success>();
        var verifiedUser = await _users.GetByIdAsync(tenant.Id, user.Id);
        verifiedUser!.EmailVerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Confirm_returns_Invalid_on_unknown_token()
    {
        await SeedUnverifiedAsync();
        var outcome = await _sut.ConfirmAsync("not-a-real-token");
        outcome.Should().BeOfType<VerifyConfirmOutcome.Invalid>();
    }

    [Fact]
    public async Task Confirm_returns_Invalid_on_expired_token()
    {
        var (tenant, user) = await SeedUnverifiedAsync();
        var plaintext = _tokenGenerator.GenerateUrlSafeBase64();
        await _authTokens.IssueEmailVerifyTokenAsync(
            tenant.Id, user.Email, _tokenHasher.Hash(plaintext),
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            TestIp, "test", default);

        var outcome = await _sut.ConfirmAsync(plaintext);

        outcome.Should().BeOfType<VerifyConfirmOutcome.Invalid>();
    }

    [Fact]
    public async Task Confirm_returns_Invalid_on_already_consumed_token()
    {
        var (tenant, user) = await SeedUnverifiedAsync();
        var token = await IssueAndExtractTokenAsync(tenant, user);

        _ = await _sut.ConfirmAsync(token);
        var second = await _sut.ConfirmAsync(token);

        second.Should().BeOfType<VerifyConfirmOutcome.Invalid>();
    }
}
