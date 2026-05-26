using DwbHub.Application.Audit;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace DwbHub.Application.Messaging;

/// <summary>
/// Hangfire-invocable orchestrator for channel history backfill.
/// Fetches Discord messages in batches of 100 using the <c>before=&lt;snowflake&gt;</c>
/// cursor (oldest snowflake of the previous batch), persisting each page and
/// advancing the cursor atomically before the next request.
///
/// Cancellation is DB-driven: the job row is re-read before every batch so that
/// a status transition to <c>Cancelled</c> is picked up even after a process restart.
///
/// Exception handling: any unhandled exception calls MarkFailed and re-throws.
/// Hangfire's default retry policy will schedule a retry; MarkRunning is COALESCE-safe
/// so a retry starting from <c>failed</c> status transitions back to <c>running</c>
/// and resumes from the last persisted cursor.
/// </summary>
public sealed class BackfillRunner : IBackfillRunner
{
    private const int BatchSize = 100;
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(200);

    private readonly IChannelBackfillJobRepository _jobs;
    private readonly IGuildChannelRepository _channels;
    private readonly IGuildBotCredentialRepository _credentials;
    private readonly IBotTokenEncryptor _encryptor;
    private readonly IDiscordRestChannelClient _discord;
    private readonly IMessageRepository _messages;
    private readonly IMessagesBroadcaster _broadcaster;
    private readonly IAuditWriter _audit;
    private readonly ILogger<BackfillRunner> _logger;

    public BackfillRunner(
        IChannelBackfillJobRepository jobs,
        IGuildChannelRepository channels,
        IGuildBotCredentialRepository credentials,
        IBotTokenEncryptor encryptor,
        IDiscordRestChannelClient discord,
        IMessageRepository messages,
        IMessagesBroadcaster broadcaster,
        IAuditWriter audit,
        ILogger<BackfillRunner> logger)
    {
        _jobs = jobs;
        _channels = channels;
        _credentials = credentials;
        _encryptor = encryptor;
        _discord = discord;
        _messages = messages;
        _broadcaster = broadcaster;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RunAsync(long tenantId, long jobId, CancellationToken ct = default)
    {
        // ── 1. Load job ───────────────────────────────────────────────────────
        var job = await _jobs.GetByIdAsync(tenantId, jobId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Backfill job {jobId} not found for tenant {tenantId}.");

        // ── 2. Transition to Running ──────────────────────────────────────────
        await _jobs.MarkRunningAsync(tenantId, jobId, ct).ConfigureAwait(false);

        // ── 3. Resolve channel ────────────────────────────────────────────────
        var bridged = await _channels.ListBridgedAsync(tenantId, ct).ConfigureAwait(false);
        var channel = bridged.FirstOrDefault(c => c.Id == job.ChannelId)
            ?? throw new InvalidOperationException(
                $"Bridged channel {job.ChannelId} not found for tenant {tenantId}.");

        // ── 4. Decrypt bot token ──────────────────────────────────────────────
        var credential = await _credentials.GetByGuildIdAsync(channel.GuildId, tenantId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No bot credential for guild {channel.GuildId} (tenant {tenantId}).");

        string botToken;
        try
        {
            botToken = _encryptor.Decrypt(
                new CipherEnvelope(credential.Nonce, credential.Ciphertext, credential.Tag));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Bot-token decryption failed for guild {GuildId} tenant {TenantId}",
                channel.GuildId, tenantId);
            await _jobs.MarkFailedAsync(tenantId, jobId, ex.Message, ct).ConfigureAwait(false);
            throw;
        }

        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: null,
            EventType: AuditEventTypes.ChannelBackfillStarted,
            Payload: new Dictionary<string, object?>
            {
                ["job_id"] = jobId,
                ["channel_id"] = job.ChannelId,
            }), ct).ConfigureAwait(false);

        // ── 5. Pagination loop ────────────────────────────────────────────────
        var cursor = job.OldestFetchedSnowflake; // null = start from most recent
        var totalFetched = job.FetchedCount;      // resume from last persisted count

        try
        {
            while (true)
            {
                // Re-read the job before each batch to detect cancellation.
                var current = await _jobs.GetByIdAsync(tenantId, jobId, ct)
                    .ConfigureAwait(false);
                if (current is null || current.Status == BackfillStatus.Cancelled)
                {
                    _logger.LogInformation(
                        "Backfill job {JobId} cancelled (tenant {TenantId}); stopping",
                        jobId, tenantId);
                    return;
                }

                var batch = await _discord.GetMessagesAsync(
                    botToken,
                    (ulong)channel.DiscordChannelId,
                    beforeSnowflake: cursor.HasValue ? (ulong)cursor.Value : null,
                    limit: BatchSize,
                    ct).ConfigureAwait(false);

                if (batch.Count == 0)
                {
                    // Channel fully backfilled.
                    break;
                }

                // Persist messages; InsertAsync returns null on duplicate snowflake.
                int batchInserted = 0;
                foreach (var dm in batch)
                {
                    var msg = new Message
                    {
                        TenantId = tenantId,
                        ChannelId = channel.Id,
                        DiscordMessageId = (long)dm.Id,
                        DiscordAuthorId = (long)dm.AuthorId,
                        DiscordAuthorName = dm.AuthorName,
                        ViaDwbhub = false,
                        Content = dm.Content,
                        SentAt = dm.SentAt,
                        EditedAt = dm.EditedAt,
                    };
                    var inserted = await _messages.InsertAsync(msg, ct).ConfigureAwait(false);
                    if (inserted is not null)
                        batchInserted++;
                }

                totalFetched += batchInserted;

                // Advance cursor to the OLDEST (min) snowflake in this batch.
                // Discord returns messages newest-first; min = last element.
                var oldestSnowflake = (long)batch.Min(m => m.Id);
                cursor = oldestSnowflake;

                await _jobs.AdvanceCursorAsync(
                    tenantId, jobId, oldestSnowflake, batchInserted, ct)
                    .ConfigureAwait(false);

                await _broadcaster.BackfillProgressAsync(
                    tenantId, channel.PublicId, jobId, totalFetched, ct)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "Backfill job {JobId}: batch done, inserted {Inserted}/{BatchSize}, " +
                    "total {Total}, cursor {Cursor}",
                    jobId, batchInserted, batch.Count, totalFetched, oldestSnowflake);

                await Task.Delay(BatchDelay, ct).ConfigureAwait(false);
            }

            // ── 6. Mark complete ───────────────────────────────────────────────
            await _jobs.MarkCompleteAsync(tenantId, jobId, totalFetched, ct)
                .ConfigureAwait(false);

            await _broadcaster.BackfillCompleteAsync(
                tenantId, channel.PublicId, jobId, totalFetched, ct)
                .ConfigureAwait(false);

            await _audit.RecordAsync(new AuditEvent(
                TenantId: tenantId,
                ActorUserId: null,
                EventType: AuditEventTypes.ChannelBackfillCompleted,
                Payload: new Dictionary<string, object?>
                {
                    ["job_id"] = jobId,
                    ["channel_id"] = job.ChannelId,
                    ["fetched_count"] = totalFetched,
                }), ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Backfill job {JobId} completed: {Total} messages fetched for channel {ChannelId}",
                jobId, totalFetched, job.ChannelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Backfill job {JobId} failed (tenant {TenantId})", jobId, tenantId);

            await _jobs.MarkFailedAsync(tenantId, jobId, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);

            await _audit.RecordAsync(new AuditEvent(
                TenantId: tenantId,
                ActorUserId: null,
                EventType: AuditEventTypes.ChannelBackfillFailed,
                Payload: new Dictionary<string, object?>
                {
                    ["job_id"] = jobId,
                    ["channel_id"] = job.ChannelId,
                    ["error"] = ex.Message,
                }), CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }
}
