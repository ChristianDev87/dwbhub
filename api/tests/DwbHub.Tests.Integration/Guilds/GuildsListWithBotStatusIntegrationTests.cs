using Dapper;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Guilds;

[Collection(DatabaseCollection.Name)]
public sealed class GuildsListWithBotStatusIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildBotCredentialRepository _credentials;
    private readonly BCryptPasswordHasher _hasher = new();

    public GuildsListWithBotStatusIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _guilds = new GuildRepository(factory);
        _credentials = new GuildBotCredentialRepository(factory);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task ListByTenantWithStatusAsync_reports_configured_flag_per_guild()
    {
        var tid = await _tenants.CreateAsync(name: "Acme", slug: "acme");
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: "owner@acme.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: "Owner", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));

        var (configuredGid, _) = await _guilds.CreateAsync(tid, "1111111111111111111", "Alpha", uid);
        var (_, _) = await _guilds.CreateAsync(tid, "2222222222222222222", "Beta", uid);

        await _credentials.UpsertAsync(configuredGid, tid, new CipherEnvelope(
            Nonce: Enumerable.Repeat((byte)0x11, 12).ToArray(),
            Ciphertext: Enumerable.Repeat((byte)0x22, 80).ToArray(),
            Tag: Enumerable.Repeat((byte)0x33, 16).ToArray()));

        var items = await _guilds.ListByTenantWithStatusAsync(tid);

        items.Should().HaveCount(2);
        items.Single(i => i.Guild.DisplayName == "Alpha").BotCredentialsConfigured.Should().BeTrue();
        items.Single(i => i.Guild.DisplayName == "Beta").BotCredentialsConfigured.Should().BeFalse();
    }
}
