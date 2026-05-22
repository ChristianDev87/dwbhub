namespace DwbHub.Core.Entities;

/// <summary>
/// Singleton row of audit_verify_state. The id column is fixed at 1 via CHECK
/// constraint; not surfaced here.
/// </summary>
public sealed record AuditVerifyState(
    long LastVerifiedId,
    byte[]? LastVerifiedHash,
    DateTimeOffset LastRunAt,
    string LastRunStatus);
