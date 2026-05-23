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

namespace DwbHub.Tests.Integration.BotCredentials;

[Collection(DatabaseCollection.Name)]
public sealed class GuildBotCredentialRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildBotCredentialRepository _sut;
    private readonly BCryptPasswordHasher _hasher = new();

    public GuildBotCredentialRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _guilds = new GuildRepository(factory);
        _sut = new GuildBotCredentialRepository(factory);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(long tenantId, long guildId)> SeedTenantOwnerAndGuildAsync()
    {
        var tid = await _tenants.CreateAsync(name: "Acme", slug: "acme");
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: "owner@acme.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: "Owner", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var (gid, _) = await _guilds.CreateAsync(
            tenantId: tid,
            discordGuildId: "1234567890123456789",
            displayName: "Production",
            registeredByUserId: uid);
        return (tid, gid);
    }

    private static CipherEnvelope FakeEnvelope(byte seed = 0x42) => new(
        Nonce: Enumerable.Repeat(seed, 12).ToArray(),
        Ciphertext: Enumerable.Repeat(seed, 80).ToArray(),
        Tag: Enumerable.Repeat(seed, 16).ToArray());

    [Fact]
    public async Task Upsert_then_GetByGuildId_returns_persisted_envelope()
    {
        var (tid, gid) = await SeedTenantOwnerAndGuildAsync();
        var envelope = FakeEnvelope(0x11);

        await _sut.UpsertAsync(gid, tid, envelope);

        var loaded = await _sut.GetByGuildIdAsync(gid, tid);
        loaded.Should().NotBeNull();
        loaded!.GuildId.Should().Be(gid);
        loaded.TenantId.Should().Be(tid);
        loaded.Nonce.Should().Equal(envelope.Nonce);
        loaded.Ciphertext.Should().Equal(envelope.Ciphertext);
        loaded.Tag.Should().Equal(envelope.Tag);
    }

    [Fact]
    public async Task Upsert_twice_replaces_envelope_and_keeps_one_row()
    {
        var (tid, gid) = await SeedTenantOwnerAndGuildAsync();
        var first = FakeEnvelope(0x11);
        var second = FakeEnvelope(0x22);

        await _sut.UpsertAsync(gid, tid, first);
        var initialExists = await _sut.ExistsForGuildAsync(gid, tid);
        await _sut.UpsertAsync(gid, tid, second);

        initialExists.Should().BeTrue();
        var loaded = await _sut.GetByGuildIdAsync(gid, tid);
        loaded!.Nonce.Should().Equal(second.Nonce);
        loaded.Ciphertext.Should().Equal(second.Ciphertext);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var count = await conn.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM guild_bot_credentials WHERE guild_id = @g",
            new { g = gid });
        count.Should().Be(1);
    }

    [Fact]
    public async Task Delete_existing_returns_true_then_Delete_again_returns_false()
    {
        var (tid, gid) = await SeedTenantOwnerAndGuildAsync();
        await _sut.UpsertAsync(gid, tid, FakeEnvelope());

        var first = await _sut.DeleteAsync(gid, tid);
        var second = await _sut.DeleteAsync(gid, tid);
        var loaded = await _sut.GetByGuildIdAsync(gid, tid);

        first.Should().BeTrue();
        second.Should().BeFalse();
        loaded.Should().BeNull();
    }
}
