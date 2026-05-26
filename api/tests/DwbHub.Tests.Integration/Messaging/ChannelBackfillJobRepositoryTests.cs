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
public sealed class ChannelBackfillJobRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _channels;
    private readonly ChannelBackfillJobRepository _sut;
    private readonly BCryptPasswordHasher _hasher = new();

    public ChannelBackfillJobRepositoryTests(PostgresFixture fixture)
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
        _sut = new ChannelBackfillJobRepository(fac);
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

    // ── InsertPendingAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task InsertPending_Returns_PendingJobWithCorrectIds()
    {
        var (tid, _, _, cid) = await SeedAsync();

        var job = await _sut.InsertPendingAsync(tid, cid);

        job.Should().NotBeNull();
        job.Id.Should().BeGreaterThan(0);
        job.TenantId.Should().Be(tid);
        job.ChannelId.Should().Be(cid);
        job.Status.Should().Be(BackfillStatus.Pending);
        job.FetchedCount.Should().Be(0);
        job.OldestFetchedSnowflake.Should().BeNull();
        job.HangfireJobId.Should().BeNull();
        job.StartedAt.Should().BeNull();
        job.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task InsertPending_OnExistingJobForChannel_ResetsStateInPlace()
    {
        // Regression: previously InsertPendingAsync used a plain INSERT and
        // threw on the UNIQUE(channel_id) constraint when a channel was
        // re-bridged after a prior bridge cycle. The fix is UPSERT: the row
        // for the channel is reused and all progress fields are cleared.
        var (tid, _, _, cid) = await SeedAsync();

        var first = await _sut.InsertPendingAsync(tid, cid);
        // Simulate a completed prior backfill
        await _sut.MarkRunningAsync(tid, first.Id);
        await _sut.AdvanceCursorAsync(tid, first.Id, oldestSnowflake: 12345L, fetchedCount: 7);
        await _sut.MarkCompleteAsync(tid, first.Id, finalCount: 7);

        // Re-bridge: InsertPendingAsync must NOT throw, and the existing row
        // must be reset to pending with cleared progress fields.
        var second = await _sut.InsertPendingAsync(tid, cid);

        second.Id.Should().Be(first.Id, "the upsert must reuse the existing row");
        second.Status.Should().Be(BackfillStatus.Pending);
        second.FetchedCount.Should().Be(0);
        second.OldestFetchedSnowflake.Should().BeNull();
        second.StartedAt.Should().BeNull();
        second.CompletedAt.Should().BeNull();
        second.HangfireJobId.Should().BeNull();
        second.LastError.Should().BeNull();
    }

    // ── GetByChannelAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetByChannel_Returns_InsertedJob()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);

        var fetched = await _sut.GetByChannelAsync(tid, cid);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(inserted.Id);
        fetched.Status.Should().Be(BackfillStatus.Pending);
    }

    [Fact]
    public async Task GetByChannel_Returns_Null_WhenNoJobExists()
    {
        var (tid, _, _, cid) = await SeedAsync();

        var result = await _sut.GetByChannelAsync(tid, cid);

        result.Should().BeNull();
    }

    // ── GetByIdAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_Returns_InsertedJob()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);

        var fetched = await _sut.GetByIdAsync(tid, inserted.Id);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(inserted.Id);
        fetched.ChannelId.Should().Be(cid);
    }

    [Fact]
    public async Task GetById_Returns_Null_WhenDifferentTenant()
    {
        var (tid1, _, _, cid1) = await SeedAsync("acme", 100000000000000111L, 100000000000000221L);
        var (tid2, _, _, _) = await SeedAsync("bravo", 100000000000000112L, 100000000000000222L);

        var inserted = await _sut.InsertPendingAsync(tid1, cid1);

        // Attempt to read with a different tenant must return null (info-leak protection).
        var result = await _sut.GetByIdAsync(tid2, inserted.Id);

        result.Should().BeNull("cross-tenant GetById must return null");
    }

    // ── SetHangfireJobIdAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task SetHangfireJobId_Persists_And_Returns_True()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);

        var result = await _sut.SetHangfireJobIdAsync(tid, inserted.Id, "hf-42");

        result.Should().BeTrue();
        var reloaded = await _sut.GetByIdAsync(tid, inserted.Id);
        reloaded!.HangfireJobId.Should().Be("hf-42");
    }

    // ── AdvanceCursorAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task AdvanceCursor_Updates_SnowflakeAndCount_Atomically()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);

        var result = await _sut.AdvanceCursorAsync(tid, inserted.Id, 99001L, 100);

        result.Should().BeTrue();
        var reloaded = await _sut.GetByIdAsync(tid, inserted.Id);
        reloaded!.OldestFetchedSnowflake.Should().Be(99001L);
        reloaded.FetchedCount.Should().Be(100);

        // Advance again — fetched_count must accumulate.
        await _sut.AdvanceCursorAsync(tid, inserted.Id, 98001L, 75);
        var reloaded2 = await _sut.GetByIdAsync(tid, inserted.Id);
        reloaded2!.OldestFetchedSnowflake.Should().Be(98001L);
        reloaded2.FetchedCount.Should().Be(175, "second advance must increment, not replace");
    }

    // ── MarkRunningAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task MarkRunning_Transitions_Pending_To_Running_Sets_StartedAt()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);

        var result = await _sut.MarkRunningAsync(tid, inserted.Id);

        result.Should().BeTrue();
        var reloaded = await _sut.GetByIdAsync(tid, inserted.Id);
        reloaded!.Status.Should().Be(BackfillStatus.Running);
        reloaded.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkRunning_OnAlreadyRunning_KeepsOriginalStartedAt()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);

        await _sut.MarkRunningAsync(tid, inserted.Id);
        var afterFirst = await _sut.GetByIdAsync(tid, inserted.Id);
        var originalStartedAt = afterFirst!.StartedAt;

        // Simulate a Hangfire retry: job is already Running.
        await _sut.MarkRunningAsync(tid, inserted.Id);
        var afterSecond = await _sut.GetByIdAsync(tid, inserted.Id);

        afterSecond!.StartedAt.Should().Be(originalStartedAt,
            "COALESCE must preserve the first started_at on Hangfire retry");
    }

    // ── MarkCompleteAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkComplete_Sets_Status_CompletedAt_And_FinalCount()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);
        await _sut.MarkRunningAsync(tid, inserted.Id);

        var result = await _sut.MarkCompleteAsync(tid, inserted.Id, 250);

        result.Should().BeTrue();
        var reloaded = await _sut.GetByIdAsync(tid, inserted.Id);
        reloaded!.Status.Should().Be(BackfillStatus.Complete);
        reloaded.CompletedAt.Should().NotBeNull();
        reloaded.FetchedCount.Should().Be(250);
    }

    // ── MarkFailedAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkFailed_Sets_Status_LastError_And_CompletedAt()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);
        await _sut.MarkRunningAsync(tid, inserted.Id);

        var result = await _sut.MarkFailedAsync(tid, inserted.Id, "Rate limited");

        result.Should().BeTrue();
        var reloaded = await _sut.GetByIdAsync(tid, inserted.Id);
        reloaded!.Status.Should().Be(BackfillStatus.Failed);
        reloaded.LastError.Should().Be("Rate limited");
        reloaded.CompletedAt.Should().NotBeNull();
    }

    // ── MarkCancelledAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task MarkCancelled_Sets_Status_And_CompletedAt()
    {
        var (tid, _, _, cid) = await SeedAsync();
        var inserted = await _sut.InsertPendingAsync(tid, cid);

        var result = await _sut.MarkCancelledAsync(tid, inserted.Id);

        result.Should().BeTrue();
        var reloaded = await _sut.GetByIdAsync(tid, inserted.Id);
        reloaded!.Status.Should().Be(BackfillStatus.Cancelled);
        reloaded.CompletedAt.Should().NotBeNull();
    }

    // ── Cross-tenant isolation ─────────────────────────────────────────────────

    [Fact]
    public async Task MarkComplete_DifferentTenant_NoOp_Returns_False()
    {
        var (tid1, _, _, cid1) = await SeedAsync("acme", 100000000000000111L, 100000000000000221L);
        var (tid2, _, _, _) = await SeedAsync("bravo", 100000000000000112L, 100000000000000222L);

        var inserted = await _sut.InsertPendingAsync(tid1, cid1);

        // Attempt to mark complete using a different tenant ID.
        var result = await _sut.MarkCompleteAsync(tid2, inserted.Id, 999);

        result.Should().BeFalse("cross-tenant MarkComplete must be no-op");

        var reloaded = await _sut.GetByIdAsync(tid1, inserted.Id);
        reloaded!.Status.Should().Be(BackfillStatus.Pending,
            "original row must remain unchanged after cross-tenant attempt");
    }
}
