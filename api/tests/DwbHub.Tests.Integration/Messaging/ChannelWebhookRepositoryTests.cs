using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Messaging;

[Collection(DatabaseCollection.Name)]
public sealed class ChannelWebhookRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _channels;
    private readonly ChannelWebhookRepository _sut;
    private readonly BCryptPasswordHasher _hasher = new();

    public ChannelWebhookRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var fac = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(fac);
        _users = new UserRepository(fac);
        _guilds = new GuildRepository(fac);
        _channels = new GuildChannelRepository(fac);
        _sut = new ChannelWebhookRepository(fac);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(long tenantId, long userId, long guildId, long channelId)> SeedAsync(
        string slug = "acme",
        long discordGuildId = 100000000000000111L,
        long discordChannelId = 100000000000000222L)
    {
        var tid = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: $"owner@{slug}.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"), DisplayName: "Owner",
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var (gid, _) = await _guilds.CreateAsync(tid, discordGuildId.ToString(), "Guild", uid);
        var chan = await _channels.UpsertFromSyncAsync(tid, gid, discordChannelId, "general", 0, 0);
        return (tid, uid, gid, chan.Id);
    }

    private static ChannelWebhook MakeWebhook(long tenantId, long channelId, long userId,
        long discordWebhookId = 77001L) => new()
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordWebhookId = discordWebhookId,
            Ciphertext = new byte[] { 1, 2, 3 },
            Nonce = new byte[] { 4, 5, 6 },
            AuthTag = new byte[] { 7, 8, 9 },
            KeyVersion = 1,
            CreatedByUserId = userId,
        };

    // ── Insert / Get ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_then_GetByChannel_returns_webhook()
    {
        var (tid, uid, _, cid) = await SeedAsync();
        var wh = MakeWebhook(tid, cid, uid);

        var inserted = await _sut.InsertAsync(wh);
        var fetched = await _sut.GetByChannelAsync(tid, cid);

        inserted.Id.Should().BeGreaterThan(0);
        inserted.TenantId.Should().Be(tid);
        inserted.ChannelId.Should().Be(cid);
        inserted.DiscordWebhookId.Should().Be(77001L);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(inserted.Id);
        fetched.Ciphertext.Should().Equal(wh.Ciphertext);
        fetched.Nonce.Should().Equal(wh.Nonce);
        fetched.AuthTag.Should().Equal(wh.AuthTag);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteByChannel_removes_webhook_and_returns_true()
    {
        var (tid, uid, _, cid) = await SeedAsync();
        await _sut.InsertAsync(MakeWebhook(tid, cid, uid));

        var deleted = await _sut.DeleteByChannelAsync(tid, cid);
        var fetched = await _sut.GetByChannelAsync(tid, cid);

        deleted.Should().BeTrue();
        fetched.Should().BeNull("webhook should no longer exist after deletion");
    }

    [Fact]
    public async Task DeleteByChannel_returns_false_when_no_row_exists()
    {
        var (tid, _, _, cid) = await SeedAsync();

        var result = await _sut.DeleteByChannelAsync(tid, cid);

        result.Should().BeFalse();
    }

    // ── Cross-tenant isolation ─────────────────────────────────────────────────

    [Fact]
    public async Task GetByChannel_isolates_by_tenant()
    {
        var (tid1, uid1, _, cid1) = await SeedAsync("acme", 100000000000000111L, 100000000000000221L);
        var (tid2, uid2, _, cid2) = await SeedAsync("bravo", 100000000000000112L, 100000000000000222L);

        await _sut.InsertAsync(MakeWebhook(tid1, cid1, uid1, 77001L));
        await _sut.InsertAsync(MakeWebhook(tid2, cid2, uid2, 77002L));

        var forA = await _sut.GetByChannelAsync(tid1, cid1);
        var forB = await _sut.GetByChannelAsync(tid2, cid2);

        forA.Should().NotBeNull();
        forA!.DiscordWebhookId.Should().Be(77001L);

        forB.Should().NotBeNull();
        forB!.DiscordWebhookId.Should().Be(77002L);

        // Cross-tenant leakage check
        var crossLeak = await _sut.GetByChannelAsync(tid1, cid2);
        crossLeak.Should().BeNull("tenant A must not see tenant B's webhook");
    }
}
