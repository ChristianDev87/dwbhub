using Dapper;
using DwbHub.Application.Audit;
using DwbHub.Application.Encryption;
using DwbHub.Application.Messaging;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Messaging;

/// <summary>
/// End-to-end integration test for <see cref="BackfillRunner"/> against real Postgres.
/// Uses a fake Discord REST client that returns three pages of 250 messages total
/// (100, 100, 50) then an empty page, and a no-op broadcaster that records calls.
/// No real crypto — the IBotTokenEncryptor is replaced by a fake that always returns
/// a known plaintext, so a real credential envelope is inserted into the DB (byte[1])
/// and the fake encryptor simply ignores it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BackfillRunnerIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _channels;
    private readonly GuildBotCredentialRepository _creds;
    private readonly MessageRepository _messages;
    private readonly ChannelBackfillJobRepository _jobs;
    private readonly BCryptPasswordHasher _hasher = new();

    public BackfillRunnerIntegrationTests(PostgresFixture fixture)
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
        _creds = new GuildBotCredentialRepository(fac);
        _messages = new MessageRepository(fac);
        _jobs = new ChannelBackfillJobRepository(fac);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns three pages (100, 100, 50) then empty. Ignores the bot token.
    /// Snowflakes go from 1050 down so oldest = 1 (min).
    /// </summary>
    private sealed class FakeDiscordRestChannelClient : IDiscordRestChannelClient
    {
        private int _callCount;

        // 100 high snowflakes, then 100 mid, then 50 low, then empty.
        private static IReadOnlyList<DiscordMessageInfo> MakeBatch(int start, int count) =>
            Enumerable.Range(start, count)
                .Select(i => new DiscordMessageInfo(
                    Id: (ulong)i,
                    AuthorId: 42UL,
                    AuthorName: "bot-user",
                    AuthorIsWebhook: false,
                    Content: $"msg-{i}",
                    SentAt: DateTimeOffset.UtcNow,
                    EditedAt: null))
                .ToList();

        public Task<IReadOnlyList<DiscordMessageInfo>> GetMessagesAsync(
            string botToken,
            ulong discordChannelId,
            ulong? beforeSnowflake,
            int limit,
            CancellationToken ct = default)
        {
            _callCount++;
            IReadOnlyList<DiscordMessageInfo> page = _callCount switch
            {
                1 => MakeBatch(951, 100),  // snowflakes 951-1050
                2 => MakeBatch(851, 100),  // snowflakes 851-950
                3 => MakeBatch(801, 50),   // snowflakes 801-850
                _ => new List<DiscordMessageInfo>(),
            };
            return Task.FromResult(page);
        }

        public Task<IReadOnlyList<DiscordChannelInfo>> ListChannelsAsync(
            string botToken, ulong discordGuildId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscordChannelInfo>>(new List<DiscordChannelInfo>());

        public Task<DiscordWebhookCreated> CreateWebhookAsync(
            string botToken, ulong discordChannelId, string name, CancellationToken ct = default) =>
            Task.FromResult(new DiscordWebhookCreated(0UL, ""));

        public Task<bool> DeleteWebhookAsync(
            ulong webhookId, string webhookToken, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<DiscordMessageInfo> ExecuteWebhookAsync(
            ulong webhookId, string webhookToken, string username,
            string content, CancellationToken ct = default) =>
            Task.FromResult(new DiscordMessageInfo(0UL, 0UL, "", false, "", DateTimeOffset.UtcNow, null));
    }

    /// <summary>
    /// Captures BackfillProgress and BackfillComplete calls for assertion.
    /// </summary>
    private sealed class CapturingBroadcaster : IMessagesBroadcaster
    {
        public List<int> ProgressCounts { get; } = new();
        public List<(long JobId, int FetchedCount)> CompleteCalls { get; } = new();

        public Task BackfillProgressAsync(long tenantId, Guid channelPublicId, long jobId, int fetchedCount, CancellationToken ct = default)
        {
            ProgressCounts.Add(fetchedCount);
            return Task.CompletedTask;
        }

        public Task BackfillCompleteAsync(long tenantId, Guid channelPublicId, long jobId, int fetchedCount, CancellationToken ct = default)
        {
            CompleteCalls.Add((jobId, fetchedCount));
            return Task.CompletedTask;
        }

        public Task MessageReceivedAsync(MessageBroadcastDto msg, Guid channelPublicId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task MessageUpdatedAsync(long tenantId, long messageId, string content, DateTimeOffset editedAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task MessageDeletedAsync(long tenantId, long messageId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ChannelBridgeChangedAsync(long tenantId, Guid channelPublicId, bool isBridged, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Always returns "fake-token" regardless of the cipher envelope passed in.
    /// </summary>
    private sealed class FakeBotTokenEncryptor : IBotTokenEncryptor
    {
        // 59 bytes satisfies the CHECK constraint: octet_length BETWEEN 50 AND 200
        public CipherEnvelope Encrypt(string plaintext) =>
            new(new byte[12], new byte[59], new byte[16]);

        public string Decrypt(CipherEnvelope envelope) => "fake-bot-token";
    }

    // ── Seed helper ────────────────────────────────────────────────────────────

    private async Task<(long tenantId, long guildId, long channelId, Guid channelPublicId)> SeedAsync()
    {
        var tid = await _tenants.CreateAsync(name: "T-backfill", slug: "backfill");
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: "owner@backfill.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"), DisplayName: "Owner",
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));

        // 17-digit snowflake to satisfy the guild CHECK constraint.
        var (gid, _) = await _guilds.CreateAsync(tid, "10000000000000001", "Test Guild", uid);

        var chan = await _channels.UpsertFromSyncAsync(tid, gid, 20000000000000001L, "general", 0, 0);

        // Make channel bridged so BackfillRunner can resolve it via ListBridgedAsync.
        await _channels.SetBridgedAsync(tid, chan.PublicId, true);
        var bridgedChan = await _channels.GetByPublicIdAsync(tid, chan.PublicId);

        // Insert a fake encrypted credential (FakeBotTokenEncryptor ignores the bytes).
        // 59-byte ciphertext satisfies CHECK(octet_length BETWEEN 50 AND 200).
        await _creds.UpsertAsync(gid, tid, new CipherEnvelope(new byte[12], new byte[59], new byte[16]));

        return (tid, gid, bridgedChan!.Id, bridgedChan.PublicId);
    }

    // ── Integration test ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_250Messages_InsertsRowsPersistsStatusBroadcasts()
    {
        var (tid, _, cid, channelPublicId) = await SeedAsync();

        // Insert a pending backfill job.
        var job = await _jobs.InsertPendingAsync(tid, cid);

        var broadcaster = new CapturingBroadcaster();
        var auditWriter = new AuditWriter(new AuditLogRepository(
            new NpgsqlConnectionFactory(NpgsqlDataSource.Create(_fixture.ConnectionString))));

        var runner = new BackfillRunner(
            _jobs,
            _channels,
            _creds,
            new FakeBotTokenEncryptor(),
            new FakeDiscordRestChannelClient(),
            _messages,
            broadcaster,
            auditWriter,
            NullLogger<BackfillRunner>.Instance);

        await runner.RunAsync(tid, job.Id);

        // ── Assert: 250 rows in messages ────────────────────────────────────────
        var history = await _messages.ListByChannelBeforeAsync(tid, cid, null, 300);
        history.Should().HaveCount(250, "all 250 messages from the 3 batches should be persisted");

        // ── Assert: job status = complete, fetched_count = 250 ──────────────────
        var finalJob = await _jobs.GetByIdAsync(tid, job.Id);
        finalJob!.Status.Should().Be(BackfillStatus.Complete);
        finalJob.FetchedCount.Should().Be(250);
        finalJob.CompletedAt.Should().NotBeNull();
        finalJob.OldestFetchedSnowflake.Should().Be(801L,
            "cursor must end on the minimum snowflake of the last non-empty batch");

        // ── Assert: broadcaster received 3 BackfillProgress + 1 BackfillComplete ─
        broadcaster.ProgressCounts.Should().Equal(100, 200, 250);
        broadcaster.CompleteCalls.Should().ContainSingle(c => c.JobId == job.Id && c.FetchedCount == 250);
    }
}
