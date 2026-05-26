namespace DwbHub.Application.Background;

/// <summary>
/// Hangfire job that verifies the SHA-256 hash chain across the entire audit_log table.
/// Intended to run on a scheduled basis (e.g. weekly) and alert on any chain break.
/// </summary>
public interface IAuditVerifyFullJob
{
    /// <summary>Execute a full sequential hash-chain verification pass over all audit log rows.</summary>
    Task RunAsync(CancellationToken ct = default);
}
