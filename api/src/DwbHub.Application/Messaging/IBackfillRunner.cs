namespace DwbHub.Application.Messaging;

/// <summary>
/// Orchestrates the Discord REST history backfill for a single channel.
/// Intended to be invoked by Hangfire — keep the method signature primitive-only
/// so Hangfire's JSON serializer round-trips it without loss.
/// </summary>
public interface IBackfillRunner
{
    /// <summary>
    /// Fetch all Discord history for the backfill job identified by
    /// <paramref name="jobId"/> (tenant-scoped to <paramref name="tenantId"/>).
    /// Uses the Discord REST <c>before=&lt;snowflake&gt;</c> cursor; persists
    /// 100 messages per batch via <see cref="IMessageRepository"/> with a 200 ms
    /// delay between batches.
    ///
    /// Cancellation is DB-driven: the cursor is re-read before each batch and
    /// the loop exits cleanly when status transitions to Cancelled.
    ///
    /// On unhandled exception: calls MarkFailed then re-throws so Hangfire's
    /// retry policy can schedule another attempt.
    /// </summary>
    Task RunAsync(long tenantId, long jobId, CancellationToken ct = default);
}
