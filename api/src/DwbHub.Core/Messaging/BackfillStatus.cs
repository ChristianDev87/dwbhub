namespace DwbHub.Core.Messaging;

/// <summary>
/// Lifecycle state of a channel-backfill Hangfire job.
/// String mapping: enum-name lower-cased (matches the CHECK constraint on
/// channel_backfill_jobs.status in migration 016).
/// </summary>
public enum BackfillStatus
{
    Pending,
    Running,
    Complete,
    Failed,
    Cancelled,
}
