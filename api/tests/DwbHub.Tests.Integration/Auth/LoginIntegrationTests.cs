using System.Data;
using System.Net;
using Dapper;
using DwbHub.Application.Auth;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Auth;

[Collection(DatabaseCollection.Name)]
public sealed class LoginIntegrationTests : IAsyncLifetime
{
    private const string Base64Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly LoginService _sut;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly BCryptPasswordHasher _hasher;
    private static readonly IPAddress TestIp = IPAddress.Parse("127.0.0.1");

    public LoginIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        var attempts = new LoginAttemptRepository(factory);
        _hasher = new BCryptPasswordHasher();
        var issuer = new JwtIssuer(Base64Key);
        _sut = new LoginService(_tenants, _users, attempts, _hasher, issuer);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(long tenantId, long userId)> SeedTenantAndUserAsync(
        string slug = "acme", string email = "alice@acme.test",
        string password = "correct horse battery staple", bool active = true)
    {
        var tenantId = await _tenants.CreateAsync("Acme Corp", slug);
        var userId = await _users.CreateAsync(new User(
            Id: 0,
            TenantId: tenantId,
            Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash(password),
            DisplayName: "Alice",
            Role: UserRole.Owner,
            IsActive: active,
            CreatedAt: default, UpdatedAt: default));
        return (tenantId, userId);
    }

    [Fact]
    public async Task Login_returns_Success_on_valid_credentials()
    {
        await SeedTenantAndUserAsync();
        var outcome = await _sut.LoginAsync("acme", "alice@acme.test", "correct horse battery staple", TestIp);
        outcome.Should().BeOfType<LoginOutcome.Success>();
        ((LoginOutcome.Success)outcome).AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_returns_InvalidCredentials_on_unknown_tenant_slug()
    {
        var outcome = await _sut.LoginAsync("nope", "any@x.test", "irrelevant", TestIp);
        outcome.Should().BeOfType<LoginOutcome.InvalidCredentials>();
    }

    [Fact]
    public async Task Login_returns_InvalidCredentials_on_unknown_email()
    {
        await SeedTenantAndUserAsync();
        var outcome = await _sut.LoginAsync("acme", "ghost@acme.test", "irrelevant", TestIp);
        outcome.Should().BeOfType<LoginOutcome.InvalidCredentials>();
    }

    [Fact]
    public async Task Login_returns_InvalidCredentials_on_wrong_password()
    {
        await SeedTenantAndUserAsync();
        var outcome = await _sut.LoginAsync("acme", "alice@acme.test", "wrong", TestIp);
        outcome.Should().BeOfType<LoginOutcome.InvalidCredentials>();
    }

    [Fact]
    public async Task Login_returns_InvalidCredentials_on_inactive_user()
    {
        await SeedTenantAndUserAsync(active: false);
        var outcome = await _sut.LoginAsync("acme", "alice@acme.test", "correct horse battery staple", TestIp);
        outcome.Should().BeOfType<LoginOutcome.InvalidCredentials>();
    }

    [Fact]
    public async Task Login_returns_LockedOut_after_5_failed_attempts_in_15min()
    {
        await SeedTenantAndUserAsync();
        for (var i = 0; i < 5; i++)
        {
            _ = await _sut.LoginAsync("acme", "alice@acme.test", "wrong", TestIp);
        }
        var outcome = await _sut.LoginAsync("acme", "alice@acme.test", "correct horse battery staple", TestIp);
        outcome.Should().BeOfType<LoginOutcome.LockedOut>();
    }

    [Fact]
    public async Task Login_lockout_is_specific_to_email_and_ip()
    {
        // Same IP, different emails — should NOT lock email B because email A failed.
        var (aliceTenantId, _) = await SeedTenantAndUserAsync(email: "alice@acme.test");
        for (var i = 0; i < 5; i++)
        {
            _ = await _sut.LoginAsync("acme", "alice@acme.test", "wrong", TestIp);
        }
        // bob is a different user in the same tenant
        await _users.CreateAsync(new User(
            Id: 0, TenantId: aliceTenantId, Email: "bob@acme.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("bob-secret"),
            DisplayName: "Bob", Role: UserRole.Admin, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var outcome = await _sut.LoginAsync("acme", "bob@acme.test", "bob-secret", TestIp);
        outcome.Should().BeOfType<LoginOutcome.Success>();
    }

    [Fact]
    public async Task Login_records_attempts_on_every_branch()
    {
        await SeedTenantAndUserAsync();
        _ = await _sut.LoginAsync("acme", "alice@acme.test", "wrong", TestIp);
        _ = await _sut.LoginAsync("acme", "alice@acme.test", "correct horse battery staple", TestIp);

        // Should be 2 rows in login_attempt_log for this email.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM login_attempt_log WHERE email = @e", conn);
        cmd.Parameters.AddWithValue("e", "alice@acme.test");
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(2);
    }

    [Fact]
    public async Task Login_success_token_is_decodable()
    {
        await SeedTenantAndUserAsync();
        var outcome = await _sut.LoginAsync("acme", "alice@acme.test", "correct horse battery staple", TestIp);
        var jwt = ((LoginOutcome.Success)outcome).AccessToken;

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);

        token.Claims.Should().Contain(c => c.Type == "tslug" && c.Value == "acme");
        token.Claims.Should().Contain(c => c.Type == "role" && c.Value == "Owner");
    }
}

/// <summary>
/// Converts Npgsql's UTC DateTime (returned for TIMESTAMPTZ) to DateTimeOffset
/// so Dapper can materialize records that use DateTimeOffset for timestamp columns.
/// </summary>
internal sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        _ => throw new InvalidCastException($"Cannot convert {value?.GetType().Name} to DateTimeOffset")
    };

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value;
}
