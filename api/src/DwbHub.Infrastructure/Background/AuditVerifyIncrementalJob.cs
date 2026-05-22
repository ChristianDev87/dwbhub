using DwbHub.Application.Audit;
using DwbHub.Application.Background;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Background;

public sealed class AuditVerifyIncrementalJob(
    IDbConnectionFactory connectionFactory,
    IAuditVerifyStateRepository stateRepo,
    AuditVerifyCore core,
    IAuditWriter auditWriter,
    ILogger<AuditVerifyIncrementalJob> logger) : IAuditVerifyIncrementalJob
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var conn = (Npgsql.NpgsqlConnection)await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var state = await stateRepo.LoadForUpdateAsync(conn, tx, ct).ConfigureAwait(false);
        var result = await core.WalkAsync(state.LastVerifiedId, state.LastVerifiedHash, ct).ConfigureAwait(false);

        switch (result)
        {
            case AuditChainResult.Ok ok when ok.RowsChecked == 0:
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return;
            case AuditChainResult.Ok ok:
                await stateRepo.UpdateProgressAsync(conn, tx, ok.LastVerifiedId, ok.LastVerifiedHash!, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                logger.LogInformation("[AuditVerifyIncremental] Verified {Rows} new rows; advance state to id={Id}",
                    ok.RowsChecked, ok.LastVerifiedId);
                return;
            case AuditChainResult.Broken broken:
                await stateRepo.MarkBrokenAsync(conn, tx, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                logger.LogError("[AuditVerifyIncremental] CHAIN BROKEN at id={Id}", broken.FirstBadId);
                await auditWriter.RecordAsync(new AuditEvent(
                    TenantId: null, ActorUserId: null,
                    EventType: "audit.chain.broken",
                    Payload: new Dictionary<string, object?>
                    {
                        ["firstBadId"] = broken.FirstBadId,
                        ["expectedHash"] = Convert.ToHexString(broken.ExpectedHash),
                        ["actualHash"] = Convert.ToHexString(broken.ActualHash),
                        ["jobMode"] = "incremental",
                    }), ct).ConfigureAwait(false);
                return;
        }
    }
}
