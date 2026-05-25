using DwbHub.Core.Messaging;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Persistence for Hangfire-tracked channel backfill jobs.
/// Every method takes a `long tenantId` to enforce tenant scoping at the
/// data layer (defense-in-depth alongside middleware).
/// The UNIQUE(channel_id) constraint on channel_backfill_jobs means only one
/// active job per channel; callers must check for an existing job before inserting.
/// </summary>
public interface IChannelBackfillJobRepository
{
    /// <summary>
    /// Loads the current backfill job for a channel. Returns null when no job
    /// has been created for this channel yet.
    /// </summary>
    Task<ChannelBackfillJob?> GetByChannelAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads a backfill job by its internal id, tenant-scoped. Returns null when
    /// the id does not exist or belongs to a different tenant (info-leak protection).
    /// </summary>
    Task<ChannelBackfillJob?> GetByIdAsync(
        long tenantId,
        long jobId,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a new job in Pending status. The caller must ensure no active job
    /// exists for the channel (enforce via UNIQUE constraint or prior GetByChannelAsync).
    /// Returns the inserted row with its generated Id.
    /// </summary>
    Task<ChannelBackfillJob> InsertPendingAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the Hangfire job ID assigned after BackgroundJob.Enqueue so the
    /// job can be cancelled from the UI. Returns true iff a row was updated.
    /// </summary>
    Task<bool> SetHangfireJobIdAsync(
        long tenantId,
        long jobId,
        string hangfireJobId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically advances the Discord snowflake cursor and increments fetched_count
    /// by <paramref name="fetchedCount"/> for one REST page batch.
    /// Both fields are updated in a single round-trip to prevent partial-progress
    /// inconsistency if the worker is interrupted between two writes.
    /// Returns true iff a row was updated.
    /// </summary>
    Task<bool> AdvanceCursorAsync(
        long tenantId,
        long jobId,
        long oldestSnowflake,
        int fetchedCount,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions status → Running and sets started_at = now().
    /// Returns true iff a row was updated.
    /// </summary>
    Task<bool> MarkRunningAsync(
        long tenantId,
        long jobId,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions status → Complete, sets completed_at = now() and records the
    /// final total fetched_count. Returns true iff a row was updated.
    /// </summary>
    Task<bool> MarkCompleteAsync(
        long tenantId,
        long jobId,
        int finalCount,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions status → Failed, sets completed_at = now() and records
    /// <paramref name="error"/> in last_error. Returns true iff a row was updated.
    /// </summary>
    Task<bool> MarkFailedAsync(
        long tenantId,
        long jobId,
        string error,
        CancellationToken ct = default);

    /// <summary>
    /// Transitions status → Cancelled, sets completed_at = now().
    /// Returns true iff a row was updated.
    /// </summary>
    Task<bool> MarkCancelledAsync(
        long tenantId,
        long jobId,
        CancellationToken ct = default);
}
