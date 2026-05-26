using DwbHub.Application.Audit;
using DwbHub.Application.Background;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Background;

/// <summary>
/// Hangfire background job that re-walks the entire audit_log chain from row 0
/// on every run to detect tampering. Registered as a recurring daily job.
/// Does NOT update the audit_verify_state row — full-scan results are logged only.
/// </summary>
public sealed class AuditVerifyFullJob(
    AuditVerifyCore core,
    IAuditWriter auditWriter,
    ILogger<AuditVerifyFullJob> logger) : IAuditVerifyFullJob
{
    /// <summary>
    /// Walk all audit_log rows from the beginning, verify the hash chain, and emit an
    /// <c>audit.chain.broken</c> event if tampering is detected.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var result = await core.WalkAsync(startAfterId: 0, startingPrevHash: null, ct).ConfigureAwait(false);
        switch (result)
        {
            case AuditChainResult.Ok ok:
                logger.LogInformation("[AuditVerifyFull] Verified {Rows} rows; chain intact", ok.RowsChecked);
                return;
            case AuditChainResult.Broken broken:
                logger.LogError("[AuditVerifyFull] CHAIN BROKEN at id={Id}", broken.FirstBadId);
                await auditWriter.RecordAsync(new AuditEvent(
                    TenantId: null, ActorUserId: null,
                    EventType: "audit.chain.broken",
                    Payload: new Dictionary<string, object?>
                    {
                        ["firstBadId"] = broken.FirstBadId,
                        ["expectedHash"] = Convert.ToHexString(broken.ExpectedHash),
                        ["actualHash"] = Convert.ToHexString(broken.ActualHash),
                        ["jobMode"] = "full",
                    }), ct).ConfigureAwait(false);
                return;
        }
    }
}
