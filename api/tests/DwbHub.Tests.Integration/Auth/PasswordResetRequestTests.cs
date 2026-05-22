using System.Net;
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
public sealed class PasswordResetRequestTests : IAsyncLifetime
{
    private static readonly IPAddress TestIp = IPAddress.Parse("127.0.0.1");

    private readonly PostgresFixture _pg;
    private readonly MailpitFixture _mail;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly PasswordResetService _sut;
    private readonly BCryptPasswordHasher _passwordHasher;

    public PasswordResetRequestTests(PostgresFixture pg, MailpitFixture mail)
    {
        _pg = pg;
        _mail = mail;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_pg.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _passwordHasher = new BCryptPasswordHasher();
        var renderer = new TemplateEmailRenderer();
        var sender = new MailKitEmailSender(_mail.SmtpHost, _mail.SmtpPort, "DwbHub <noreply@test.local>");
        _sut = new PasswordResetService(
            new AuthTokenRepository(factory), _tenants, _users,
            new TokenHasher(), new TokenGenerator(), _passwordHasher, renderer, sender,
            publicBaseUrl: "http://localhost:5173");
    }

    public async Task InitializeAsync()
    {
        await _pg.ResetAsync();
        await _mail.ResetAsync();
    }
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(Tenant, User)> SeedVerifiedAsync()
    {
        var tenantId = await _tenants.CreateAsync("Acme", "acme");
        var tenant = await _tenants.GetByIdAsync(tenantId) ?? throw new InvalidOperationException();
        var userId = await _users.CreateAsync(new User(
            Id: 0, TenantId: tenantId,
            Email: "alice@acme.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _passwordHasher.Hash("pw12345678"),
            DisplayName: "Alice", Role: UserRole.Member, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = await _users.GetByIdAsync(tenantId, userId) ?? throw new InvalidOperationException();
        return (tenant, user);
    }

    [Fact]
    public async Task Request_sends_email_for_verified_user()
    {
        var (tenant, user) = await SeedVerifiedAsync();
        await _sut.RequestAsync(tenant.Slug, user.Email, "de", TestIp, null);

        var messages = await _mail.GetMessagesAsync();
        messages.Should().HaveCount(1);
        messages[0].Subject.Should().Contain("Passwort");
    }

    [Fact]
    public async Task Request_silently_skips_for_unknown_email()
    {
        var (tenant, _) = await SeedVerifiedAsync();
        await _sut.RequestAsync(tenant.Slug, "ghost@nowhere.test", "de", TestIp, null);

        var messages = await _mail.GetMessagesAsync();
        messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_rate_limits_at_4th_attempt_in_15min()
    {
        var (tenant, user) = await SeedVerifiedAsync();
        await _sut.RequestAsync(tenant.Slug, user.Email, "de", TestIp, null);
        await _sut.RequestAsync(tenant.Slug, user.Email, "de", TestIp, null);
        await _sut.RequestAsync(tenant.Slug, user.Email, "de", TestIp, null);
        await _sut.RequestAsync(tenant.Slug, user.Email, "de", TestIp, null);

        var messages = await _mail.GetMessagesAsync();
        messages.Should().HaveCount(3);
    }
}
