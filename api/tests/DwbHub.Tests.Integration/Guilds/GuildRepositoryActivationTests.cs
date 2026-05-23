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
public sealed class GuildRepositoryActivationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildBotCredentialRepository _credentials;
    private readonly BCryptPasswordHasher _hasher = new();

    public GuildRepositoryActivationTests(PostgresFixture fixture)
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

    private static CipherEnvelope FakeEnvelope(byte seed = 0x42) => new(
        Nonce: Enumerable.Repeat(seed, 12).ToArray(),
        Ciphertext: Enumerable.Repeat(seed, 80).ToArray(),
        Tag: Enumerable.Repeat(seed, 16).ToArray());

    /// <summary>
    /// Seeds: 1 tenant, 1 owner, 3 guilds:
    ///   guildA — is_active=true  + has credentials  → should appear in ListActiveWithCredentials
    ///   guildB — is_active=true  + no credentials   → should NOT appear
    ///   guildC — is_active=false + has credentials  → should NOT appear
    /// Returns the internal guild IDs for all three.
    /// </summary>
    private async Task<(long tid, long guildA, long guildB, long guildC)> SeedAsync()
    {
        var tid = await _tenants.CreateAsync(name: "Activation Tenant", slug: "activation-test");
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: "owner@activation-test.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("activation-test-password"),
            DisplayName: "Owner", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));

        // guildA: active + credentials
        var (gidA, _) = await _guilds.CreateAsync(
            tenantId: tid,
            discordGuildId: "1111111111111111111",
            displayName: "GuildA-ActiveWithCreds",
            registeredByUserId: uid);
        var envelope = FakeEnvelope(0x11);
        await _credentials.UpsertAsync(gidA, tid, envelope);

        // guildB: active + no credentials
        var (gidB, _) = await _guilds.CreateAsync(
            tenantId: tid,
            discordGuildId: "2222222222222222222",
            displayName: "GuildB-ActiveNoCreds",
            registeredByUserId: uid);

        // guildC: inactive + credentials
        var (gidC, _) = await _guilds.CreateAsync(
            tenantId: tid,
            discordGuildId: "3333333333333333333",
            displayName: "GuildC-InactiveWithCreds",
            registeredByUserId: uid);
        await _credentials.UpsertAsync(gidC, tid, FakeEnvelope(0x33));
        // set guildC inactive
        await _guilds.SetActiveAsync(gidC, tid, isActive: false);

        return (tid, gidA, gidB, gidC);
    }

    [Fact]
    public async Task ListActiveWithCredentials_returns_only_active_AND_credentialed()
    {
        var (tid, gidA, gidB, gidC) = await SeedAsync();

        var pairs = await _guilds.ListActiveWithCredentialsAsync();

        pairs.Should().HaveCount(1);
        pairs[0].GuildId.Should().Be(gidA);
        pairs[0].TenantId.Should().Be(tid);
    }

    [Fact]
    public async Task SetActiveAsync_returns_true_only_on_actual_change()
    {
        var (tid, gidA, _, _) = await SeedAsync();

        // guildA starts active (default). Setting inactive → true (actual change).
        var changed1 = await _guilds.SetActiveAsync(gidA, tid, isActive: false);
        changed1.Should().BeTrue();

        // Setting inactive again (no change) → false.
        var changed2 = await _guilds.SetActiveAsync(gidA, tid, isActive: false);
        changed2.Should().BeFalse();

        // Setting active → true (actual change).
        var changed3 = await _guilds.SetActiveAsync(gidA, tid, isActive: true);
        changed3.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateLastConnectedAtAsync_persists_timestamp()
    {
        var (_, gidA, _, _) = await SeedAsync();

        var ts = DateTimeOffset.UtcNow;
        await _guilds.UpdateLastConnectedAtAsync(gidA, ts);

        var loaded = await _guilds.GetByIdAsync(gidA);
        loaded.Should().NotBeNull();
        loaded!.LastConnectedAt.Should().NotBeNull();
        // Allow a 1-second tolerance for DB rounding
        loaded.LastConnectedAt!.Value.Should().BeCloseTo(ts, TimeSpan.FromSeconds(1));
    }
}
