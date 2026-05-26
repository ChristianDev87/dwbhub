using DwbHub.Application.Audit;
using DwbHub.Application.Encryption;
using DwbHub.Application.Messaging;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DwbHub.Tests.Unit.Messaging;

/// <summary>
/// Unit tests for <see cref="BackfillRunner"/>. All I/O dependencies are mocked.
/// Covers: empty channel, multi-batch, cancellation, exception, dedup, cursor
/// monotonicity.
/// </summary>
public sealed class BackfillRunnerTests
{
    // ── Constants ──────────────────────────────────────────────────────────────

    private const long TenantId = 1L;
    private const long JobId = 10L;
    private const long ChannelId = 99L;
    private const long GuildId = 50L;
    private const long DiscordChannelId = 555L;
    private static readonly Guid ChannelPublicId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static ChannelBackfillJob MakeJob(
        BackfillStatus status = BackfillStatus.Running,
        long? cursor = null,
        int fetchedCount = 0) => new()
        {
            Id = JobId,
            TenantId = TenantId,
            ChannelId = ChannelId,
            Status = status,
            FetchedCount = fetchedCount,
            OldestFetchedSnowflake = cursor,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static GuildChannel MakeChannel() => new()
    {
        Id = ChannelId,
        TenantId = TenantId,
        GuildId = GuildId,
        PublicId = ChannelPublicId,
        DiscordChannelId = DiscordChannelId,
        Name = "general",
        IsBridged = true,
        ChannelType = 0,
        Position = 0,
        LastSyncedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GuildBotCredential MakeCredential() => new(
        Id: 1,
        GuildId: GuildId,
        TenantId: TenantId,
        Nonce: new byte[12],
        Ciphertext: new byte[32],
        Tag: new byte[16],
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static DiscordMessageInfo MakeDiscordMessage(ulong id, string content = "msg") =>
        new(Id: id, AuthorId: 100UL, AuthorName: "user",
            AuthorIsWebhook: false, Content: content,
            SentAt: DateTimeOffset.UtcNow, EditedAt: null);

    private static IReadOnlyList<DiscordMessageInfo> MakeBatch(params ulong[] ids) =>
        ids.Select(id => MakeDiscordMessage(id)).ToList();

    private (BackfillRunner Sut,
             Mock<IChannelBackfillJobRepository> Jobs,
             Mock<IGuildChannelRepository> Channels,
             Mock<IGuildBotCredentialRepository> Creds,
             Mock<IBotTokenEncryptor> Encryptor,
             Mock<IDiscordRestChannelClient> Discord,
             Mock<IMessageRepository> Messages,
             Mock<IMessagesBroadcaster> Broadcaster,
             Mock<IAuditWriter> Audit)
        Build(ChannelBackfillJob? initialJob = null)
    {
        var jobs = new Mock<IChannelBackfillJobRepository>();
        var channels = new Mock<IGuildChannelRepository>();
        var creds = new Mock<IGuildBotCredentialRepository>();
        var encryptor = new Mock<IBotTokenEncryptor>();
        var discord = new Mock<IDiscordRestChannelClient>();
        var messages = new Mock<IMessageRepository>();
        var broadcaster = new Mock<IMessagesBroadcaster>();
        var audit = new Mock<IAuditWriter>();

        var job = initialJob ?? MakeJob();

        // Default: GetByIdAsync returns the job (for initial load + cancellation re-read)
        jobs.Setup(j => j.GetByIdAsync(TenantId, JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        jobs.Setup(j => j.MarkRunningAsync(TenantId, JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(j => j.MarkCompleteAsync(TenantId, JobId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(j => j.MarkFailedAsync(TenantId, JobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(j => j.AdvanceCursorAsync(TenantId, JobId, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        channels.Setup(c => c.ListBridgedAsync(TenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<GuildChannel> { MakeChannel() });

        creds.Setup(c => c.GetByGuildIdAsync(GuildId, TenantId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(MakeCredential());

        encryptor.Setup(e => e.Decrypt(It.IsAny<CipherEnvelope>()))
                 .Returns("fake-bot-token");

        // Default: InsertAsync always returns the message (no dedup)
        messages.Setup(m => m.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Message m, CancellationToken _) => m with { Id = 1 });

        audit.Setup(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1L);
        broadcaster.Setup(b => b.BackfillProgressAsync(
            It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        broadcaster.Setup(b => b.BackfillCompleteAsync(
            It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new BackfillRunner(
            jobs.Object, channels.Object, creds.Object, encryptor.Object,
            discord.Object, messages.Object, broadcaster.Object,
            audit.Object, NullLogger<BackfillRunner>.Instance);

        return (sut, jobs, channels, creds, encryptor, discord, messages, broadcaster, audit);
    }

    // ── Test 1: Empty channel ─────────────────────────────────────────────────

    [Fact]
    public async Task EmptyChannel_MarkCompleteWithZero_NoBroadcastProgress_OneCompleteEvent()
    {
        var (sut, jobs, _, _, _, discord, messages, broadcaster, _) = Build();

        // REST returns empty list on first call → done immediately.
        discord.Setup(d => d.GetMessagesAsync(
            It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscordMessageInfo>());

        await sut.RunAsync(TenantId, JobId);

        jobs.Verify(j => j.MarkCompleteAsync(TenantId, JobId, 0, It.IsAny<CancellationToken>()), Times.Once);
        jobs.Verify(j => j.AdvanceCursorAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        broadcaster.Verify(b => b.BackfillProgressAsync(
            It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        broadcaster.Verify(b => b.BackfillCompleteAsync(
            TenantId, ChannelPublicId, JobId, 0, It.IsAny<CancellationToken>()), Times.Once);
        messages.Verify(m => m.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test 2: 250 messages across 3 batches (100, 100, 50) ──────────────────

    [Fact]
    public async Task ThreeBatches_250Messages_CorrectProgressAndComplete()
    {
        var (sut, jobs, _, _, _, discord, _, broadcaster, _) = Build();

        // Snowflakes: batch1 = 1000–1099, batch2 = 900–999, batch3 = 850–899, then empty.
        var batch1 = Enumerable.Range(1000, 100).Select(i => MakeDiscordMessage((ulong)i)).ToList();
        var batch2 = Enumerable.Range(900, 100).Select(i => MakeDiscordMessage((ulong)i)).ToList();
        var batch3 = Enumerable.Range(850, 50).Select(i => MakeDiscordMessage((ulong)i)).ToList();

        var callCount = 0;
        discord.Setup(d => d.GetMessagesAsync(
            It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => (IReadOnlyList<DiscordMessageInfo>)batch1,
                    2 => batch2,
                    3 => batch3,
                    _ => new List<DiscordMessageInfo>(),
                };
            });

        await sut.RunAsync(TenantId, JobId);

        jobs.Verify(j => j.AdvanceCursorAsync(TenantId, JobId, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        jobs.Verify(j => j.MarkCompleteAsync(TenantId, JobId, 250, It.IsAny<CancellationToken>()), Times.Once);

        var progressCounts = new List<int>();
        broadcaster.Verify(b => b.BackfillProgressAsync(
            TenantId, ChannelPublicId, JobId, Capture.In(progressCounts), It.IsAny<CancellationToken>()),
            Times.Exactly(3));

        progressCounts.Should().Equal(100, 200, 250);

        broadcaster.Verify(b => b.BackfillCompleteAsync(TenantId, ChannelPublicId, JobId, 250, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test 3: Cancellation between batches ──────────────────────────────────

    [Fact]
    public async Task CancelledBetweenBatches_NoMarkCompleteNoMarkFailed_ExitsCleanly()
    {
        var (sut, jobs, _, _, _, discord, _, _, _) = Build();

        var batch1 = Enumerable.Range(1000, 100).Select(i => MakeDiscordMessage((ulong)i)).ToList();

        // First call returns 100 messages; after that GetByIdAsync returns Cancelled.
        var firstBatchDone = false;
        discord.Setup(d => d.GetMessagesAsync(
            It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => firstBatchDone
                ? (IReadOnlyList<DiscordMessageInfo>)new List<DiscordMessageInfo>()
                : batch1)
            .Callback(() => firstBatchDone = true);

        var callIndex = 0;
        jobs.Setup(j => j.GetByIdAsync(TenantId, JobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callIndex++;
                // First call (initial load) = Running; second call (before batch 2) = Cancelled.
                return callIndex <= 1
                    ? MakeJob(BackfillStatus.Running)
                    : MakeJob(BackfillStatus.Cancelled);
            });

        await sut.RunAsync(TenantId, JobId);

        jobs.Verify(j => j.MarkCompleteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        jobs.Verify(j => j.MarkFailedAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test 4: Discord REST throws on second batch ────────────────────────────

    [Fact]
    public async Task DiscordThrowsOnSecondBatch_MarkFailedAndRethrows()
    {
        var (sut, jobs, _, _, _, discord, _, _, _) = Build();

        var batch1 = Enumerable.Range(1000, 100).Select(i => MakeDiscordMessage((ulong)i)).ToList();
        var callCount = 0;

        discord.Setup(d => d.GetMessagesAsync(
            It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) return (IReadOnlyList<DiscordMessageInfo>)batch1;
                throw new DiscordRateLimitException(5, "Rate limited");
            });

        Func<Task> act = () => sut.RunAsync(TenantId, JobId);

        await act.Should().ThrowAsync<DiscordRateLimitException>();

        jobs.Verify(j => j.MarkFailedAsync(TenantId, JobId, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        jobs.Verify(j => j.MarkCompleteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Test 5: Duplicate snowflakes (dedup) ──────────────────────────────────

    [Fact]
    public async Task DuplicateSnowflakes_OnlyCountsNonDuplicates()
    {
        var (sut, jobs, _, _, _, discord, messages, broadcaster, _) = Build();

        // 100 messages but 30 will be duplicates (InsertAsync returns null).
        var batch = Enumerable.Range(1000, 100).Select(i => MakeDiscordMessage((ulong)i)).ToList();
        var duplicateIds = new HashSet<ulong>(batch.Take(30).Select(m => m.Id));

        discord.Setup(d => d.GetMessagesAsync(
            It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, ulong _, ulong? _, int _, CancellationToken _) =>
                (IReadOnlyList<DiscordMessageInfo>)batch);

        var batchDone = false;
        discord.Setup(d => d.GetMessagesAsync(
            It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (batchDone) return (IReadOnlyList<DiscordMessageInfo>)new List<DiscordMessageInfo>();
                batchDone = true;
                return batch;
            });

        messages.Setup(m => m.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Message msg, CancellationToken _) =>
                    duplicateIds.Contains((ulong)msg.DiscordMessageId)
                        ? null
                        : msg with { Id = 1 });

        await sut.RunAsync(TenantId, JobId);

        // AdvanceCursorAsync should be called with fetchedCount = 70 (100 - 30 duplicates).
        jobs.Verify(j => j.AdvanceCursorAsync(TenantId, JobId, It.IsAny<long>(), 70, It.IsAny<CancellationToken>()),
            Times.Once);

        // MarkComplete with totalFetched = 70.
        jobs.Verify(j => j.MarkCompleteAsync(TenantId, JobId, 70, It.IsAny<CancellationToken>()),
            Times.Once);

        // BackfillProgress broadcast should show 70, not 100.
        broadcaster.Verify(b => b.BackfillProgressAsync(TenantId, ChannelPublicId, JobId, 70, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test 6: Cursor monotonicity — min(snowflake) of batch ─────────────────

    [Fact]
    public async Task CursorIsMinSnowflakeOfBatch_NotMax()
    {
        var (sut, jobs, _, _, _, discord, _, _, _) = Build();

        // Intentionally scrambled order: 500, 100, 300. Min = 100.
        var scrambledBatch = new List<DiscordMessageInfo>
        {
            MakeDiscordMessage(500UL),
            MakeDiscordMessage(100UL),
            MakeDiscordMessage(300UL),
        };

        var batchDone = false;
        discord.Setup(d => d.GetMessagesAsync(
            It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (batchDone) return (IReadOnlyList<DiscordMessageInfo>)new List<DiscordMessageInfo>();
                batchDone = true;
                return scrambledBatch;
            });

        await sut.RunAsync(TenantId, JobId);

        // oldestSnowflake passed to AdvanceCursorAsync MUST be 100 (min), not 500 (max).
        jobs.Verify(j => j.AdvanceCursorAsync(TenantId, JobId, 100L, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
        jobs.Verify(j => j.AdvanceCursorAsync(TenantId, JobId, 500L, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
