namespace DwbHub.Application.Background;

/// <summary>
/// Result of an audit-chain integrity verification pass. Returned by the
/// incremental and full audit-verify Hangfire jobs.
/// </summary>
public abstract record AuditChainResult
{
    /// <summary>All rows in the verified range had matching hash-chain links.</summary>
    /// <param name="RowsChecked">Total number of rows verified in this pass.</param>
    /// <param name="LastVerifiedId">Row id of the last successfully verified entry.</param>
    /// <param name="LastVerifiedHash">Hash of the last verified row, for chaining into the next pass.</param>
    public sealed record Ok(long RowsChecked, long LastVerifiedId, byte[]? LastVerifiedHash) : AuditChainResult;
    /// <summary>A hash mismatch was detected — the chain has been tampered with or corrupted.</summary>
    /// <param name="FirstBadId">Row id of the first entry where the hash did not match.</param>
    /// <param name="ExpectedHash">The hash value that was expected based on the chain.</param>
    /// <param name="ActualHash">The hash value stored in the row.</param>
    public sealed record Broken(long FirstBadId, byte[] ExpectedHash, byte[] ActualHash) : AuditChainResult;
}
