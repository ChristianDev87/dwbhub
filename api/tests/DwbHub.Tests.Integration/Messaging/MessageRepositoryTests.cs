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
public sealed class MessageRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _channels;
    private readonly MessageRepository _sut;
    private readonly BCryptPasswordHasher _hasher = new();

    public MessageRepositoryTests(PostgresFixture fixture)
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
        _sut = new MessageRepository(fac);
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

    private static Message MakeMessage(long tenantId, long channelId,
        long discordMessageId = 99001L, string content = "hello") => new()
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordMessageId = discordMessageId,
            DiscordAuthorId = 55000L,
            DiscordAuthorName = "alice",
            ViaDwbhub = false,
            Content = content,
            SentAt = DateTimeOffset.UtcNow,
        };

    // ── Insert / idempotency ───────────────────────────────────────────────────

    [Fact]
    public async Task Insert_then_reload_via_List()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var msg = MakeMessage(tid, cid);

        var inserted = await _sut.InsertAsync(msg);

        inserted.Should().NotBeNull();
        inserted!.Id.Should().BeGreaterThan(0);
        inserted.TenantId.Should().Be(tid);
        inserted.ChannelId.Should().Be(cid);
        inserted.DiscordMessageId.Should().Be(99001L);
        inserted.Content.Should().Be("hello");
        inserted.ViaDwbhub.Should().BeFalse();
        inserted.DeletedAt.Should().BeNull();

        var page = await _sut.ListByChannelBeforeAsync(tid, cid, null, 50);
        page.Should().ContainSingle(m => m.Id == inserted.Id);
    }

    [Fact]
    public async Task Insert_duplicate_snowflake_returns_null()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var msg = MakeMessage(tid, cid, 99001L);

        var first = await _sut.InsertAsync(msg);
        var second = await _sut.InsertAsync(msg); // same discord_message_id

        first.Should().NotBeNull();
        second.Should().BeNull("ON CONFLICT DO NOTHING returns nothing for duplicates");
    }

    [Fact]
    public async Task ApplyEdit_updates_content_and_edited_at()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = (await _sut.InsertAsync(MakeMessage(tid, cid)))!;
        var editedAt = DateTimeOffset.UtcNow.AddSeconds(10);

        var updated = await _sut.ApplyEditAsync(tid, inserted.DiscordMessageId, "updated content", editedAt);

        updated.Should().BeTrue();

        var page = await _sut.ListByChannelBeforeAsync(tid, cid, null, 50);
        var reloaded = page.Single(m => m.Id == inserted.Id);
        reloaded.Content.Should().Be("updated content");
        reloaded.EditedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkDeleted_soft_deletes_and_hides_from_list()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = (await _sut.InsertAsync(MakeMessage(tid, cid)))!;

        var deleted = await _sut.MarkDeletedAsync(tid, inserted.DiscordMessageId);

        deleted.Should().BeTrue();

        // Soft-deleted messages must not appear in the history list
        var page = await _sut.ListByChannelBeforeAsync(tid, cid, null, 50);
        page.Should().NotContain(m => m.Id == inserted.Id);
    }

    // ── Cross-tenant isolation ─────────────────────────────────────────────────

    [Fact]
    public async Task List_isolates_by_tenant()
    {
        var (tid1, _, _, cid1) = await SeedAsync("acme", 100000000000000111L, 100000000000000221L);
        var (tid2, _, _, cid2) = await SeedAsync("bravo", 100000000000000112L, 100000000000000222L);

        await _sut.InsertAsync(MakeMessage(tid1, cid1, 10001L, "tenant-a message"));
        await _sut.InsertAsync(MakeMessage(tid2, cid2, 10002L, "tenant-b message"));

        var forA = await _sut.ListByChannelBeforeAsync(tid1, cid1, null, 50);
        var forB = await _sut.ListByChannelBeforeAsync(tid2, cid2, null, 50);

        forA.Should().ContainSingle(m => m.Content == "tenant-a message");
        forA.Should().NotContain(m => m.Content == "tenant-b message");
        forB.Should().ContainSingle(m => m.Content == "tenant-b message");
        forB.Should().NotContain(m => m.Content == "tenant-a message");
    }
}
