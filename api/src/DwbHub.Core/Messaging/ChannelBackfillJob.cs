namespace DwbHub.Core.Messaging;

/// <summary>
/// Tracks the state of a Hangfire-backed backfill run for one bridged channel.
/// One active job per channel enforced by the UNIQUE(channel_id) constraint
/// in migration 016. `OldestFetchedSnowflake` is the Discord snowflake cursor
/// passed to `before=` in subsequent REST page requests (Plan 1.0 §3.2).
/// </summary>
public sealed record ChannelBackfillJob
{
    public long Id { get; init; }
    public long TenantId { get; init; }
    public long ChannelId { get; init; }
    public BackfillStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int FetchedCount { get; init; }
    public long? OldestFetchedSnowflake { get; init; }  // pagination cursor for `before=`
    public string? LastError { get; init; }
    public string? HangfireJobId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
