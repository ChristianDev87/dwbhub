using System.Security.Cryptography;
using DwbHub.Application.Audit;
using DwbHub.Application.Background;
using DwbHub.Core.Repositories;

namespace DwbHub.Infrastructure.Background;

/// <summary>
/// Shared chain-walk used by both AuditVerifyIncrementalJob and AuditVerifyFullJob.
/// Walks audit_log rows starting after a given id, recomputes each row's expected
/// hash, and returns Ok or Broken. The caller is responsible for state-row updates
/// (incremental) or skipping them (full).
///
/// NOTE: row.PayloadJson is the postgres-formatted `payload_json::text` output
/// (with spaces after colons). This is passed AS-IS to HashEvent — the SQL insert
/// in AuditLogRepository computes the hash using the same Postgres-formatted text
/// embedded in the wrapper, so the C# wrapper here matches byte-for-byte.
/// </summary>
public sealed class AuditVerifyCore(IAuditLogRepository auditLog)
{
    public async Task<AuditChainResult> WalkAsync(
        long startAfterId, byte[]? startingPrevHash,
        CancellationToken ct = default)
    {
        var prev = startingPrevHash;
        long rowsChecked = 0;
        long lastVerifiedId = startAfterId;
        byte[]? lastVerifiedHash = startingPrevHash;

        await foreach (var row in auditLog.StreamAscAsync(startAfterId, ct).ConfigureAwait(false))
        {
            var payloadHash = CanonicalJsonSerializer.HashEvent(
                row.TenantId, row.ActorUserId, row.EventType,
                row.PayloadJson, row.IpAddress, row.UserAgent, row.OccurredAt);

            var prevBytes = prev ?? Array.Empty<byte>();
            var combined = new byte[prevBytes.Length + payloadHash.Length];
            prevBytes.CopyTo(combined, 0);
            payloadHash.CopyTo(combined, prevBytes.Length);
            var expected = SHA256.HashData(combined);

            if (!expected.AsSpan().SequenceEqual(row.CurrentHash))
            {
                return new AuditChainResult.Broken(row.Id, expected, row.CurrentHash);
            }

            prev = row.CurrentHash;
            lastVerifiedId = row.Id;
            lastVerifiedHash = row.CurrentHash;
            rowsChecked++;
        }

        return new AuditChainResult.Ok(rowsChecked, lastVerifiedId, lastVerifiedHash);
    }
}
