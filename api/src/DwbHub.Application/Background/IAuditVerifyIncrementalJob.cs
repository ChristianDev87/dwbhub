namespace DwbHub.Application.Background;

/// <summary>
/// Hangfire job that verifies only the audit_log rows not yet covered by the last
/// incremental pass. Intended to run frequently (e.g. hourly) to detect recent tampering.
/// </summary>
public interface IAuditVerifyIncrementalJob
{
    /// <summary>Execute an incremental hash-chain verification pass from the last verified row id.</summary>
    Task RunAsync(CancellationToken ct = default);
}
