using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Messaging;

[Collection(DatabaseCollection.Name)]
public sealed class GuildChannelRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _sut;
    private readonly BCryptPasswordHasher _hasher = new();

    public GuildChannelRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var fac = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(fac);
        _users = new UserRepository(fac);
        _guilds = new GuildRepository(fac);
        _sut = new GuildChannelRepository(fac);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(long tenantId, long userId, long guildId)> SeedGuildAsync(
        string slug = "acme", long discordGuildId = 100000000000000111L)
    {
        var tid = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: $"owner@{slug}.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"), DisplayName: "Owner",
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var (gid, _) = await _guilds.CreateAsync(tid, discordGuildId.ToString(), "Guild", uid);
        return (tid, uid, gid);
    }

    // ── Upsert ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertFromSync_inserts_and_returns_channel()
    {
        var (tid, _, gid) = await SeedGuildAsync();

        var channel = await _sut.UpsertFromSyncAsync(tid, gid, 500L, "general", 0, 1);

        channel.Should().NotBeNull();
        channel.Id.Should().BeGreaterThan(0);
        channel.TenantId.Should().Be(tid);
        channel.GuildId.Should().Be(gid);
        channel.DiscordChannelId.Should().Be(500L);
        channel.Name.Should().Be("general");
        channel.ChannelType.Should().Be(0);
        channel.Position.Should().Be(1);
        channel.IsBridged.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertFromSync_updates_name_and_position_on_conflict()
    {
        var (tid, _, gid) = await SeedGuildAsync();

        var first = await _sut.UpsertFromSyncAsync(tid, gid, 500L, "general", 0, 1);
        var second = await _sut.UpsertFromSyncAsync(tid, gid, 500L, "general-renamed", 0, 3);

        second.Id.Should().Be(first.Id, "same row should be returned");
        second.Name.Should().Be("general-renamed");
        second.Position.Should().Be(3);
        second.IsBridged.Should().BeFalse("upsert must not change bridge state");
        second.BridgedAt.Should().BeNull("upsert must not set bridged_at");
    }

    // ── SetBridged ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetBridged_true_preserves_first_bridge_timestamp()
    {
        var (tid, _, gid) = await SeedGuildAsync();
        var channel = await _sut.UpsertFromSyncAsync(tid, gid, 500L, "general", 0, 0);

        // Bridge for the first time
        var result1 = await _sut.SetBridgedAsync(tid, channel.PublicId, true);
        var afterFirst = await _sut.GetByPublicIdAsync(tid, channel.PublicId);

        result1.Should().BeTrue();
        afterFirst!.IsBridged.Should().BeTrue();
        afterFirst.BridgedAt.Should().NotBeNull();

        var firstStamp = afterFirst.BridgedAt!.Value;

        // Bridge again — bridged_at must not change
        await _sut.SetBridgedAsync(tid, channel.PublicId, true);
        var afterSecond = await _sut.GetByPublicIdAsync(tid, channel.PublicId);

        afterSecond!.BridgedAt.Should().Be(firstStamp, "first-bridge timestamp must be preserved");
    }

    [Fact]
    public async Task SetBridged_false_clears_is_bridged_but_keeps_bridged_at()
    {
        var (tid, _, gid) = await SeedGuildAsync();
        var channel = await _sut.UpsertFromSyncAsync(tid, gid, 500L, "general", 0, 0);
        await _sut.SetBridgedAsync(tid, channel.PublicId, true);
        var bridgedState = await _sut.GetByPublicIdAsync(tid, channel.PublicId);
        var stamp = bridgedState!.BridgedAt!.Value;

        await _sut.SetBridgedAsync(tid, channel.PublicId, false);
        var unbridged = await _sut.GetByPublicIdAsync(tid, channel.PublicId);

        unbridged!.IsBridged.Should().BeFalse();
        unbridged.BridgedAt.Should().Be(stamp, "audit timestamp must not be cleared on un-bridge");
    }

    // ── Cross-tenant isolation ─────────────────────────────────────────────────

    [Fact]
    public async Task ListByGuild_isolates_by_tenant()
    {
        var (tid1, _, gid1) = await SeedGuildAsync("acme", 100000000000000111L);
        var (tid2, _, gid2) = await SeedGuildAsync("bravo", 100000000000000112L);

        await _sut.UpsertFromSyncAsync(tid1, gid1, 501L, "alpha-channel", 0, 0);
        await _sut.UpsertFromSyncAsync(tid2, gid2, 502L, "bravo-channel", 0, 0);

        var forA = await _sut.ListByGuildAsync(tid1, gid1);
        var forB = await _sut.ListByGuildAsync(tid2, gid2);

        forA.Should().ContainSingle(c => c.Name == "alpha-channel");
        forA.Should().NotContain(c => c.Name == "bravo-channel");
        forB.Should().ContainSingle(c => c.Name == "bravo-channel");
        forB.Should().NotContain(c => c.Name == "alpha-channel");
    }
}
