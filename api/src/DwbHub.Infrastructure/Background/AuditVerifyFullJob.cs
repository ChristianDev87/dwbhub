using DwbHub.Application.Audit;
using DwbHub.Application.Background;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Background;

public sealed class AuditVerifyFullJob(
    AuditVerifyCore core,
    IAuditWriter auditWriter,
    ILogger<AuditVerifyFullJob> logger) : IAuditVerifyFullJob
{
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
