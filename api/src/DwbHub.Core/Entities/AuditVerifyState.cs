namespace DwbHub.Core.Entities;

/// <summary>
/// Singleton row of audit_verify_state. The id column is fixed at 1 via CHECK
/// constraint; not surfaced here.
/// </summary>
/// <param name="LastVerifiedId">Audit-log row id up to which the chain has been verified.</param>
/// <param name="LastVerifiedHash">Hash of the last verified row, used as the starting point for the next run. Null before the first run.</param>
/// <param name="LastRunAt">Timestamp of the most recent integrity-check job execution.</param>
/// <param name="LastRunStatus">Outcome of the last run: <c>"ok"</c> or <c>"broken"</c>.</param>
public sealed record AuditVerifyState(
    long LastVerifiedId,
    byte[]? LastVerifiedHash,
    DateTimeOffset LastRunAt,
    string LastRunStatus);
