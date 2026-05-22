namespace DwbHub.Application.Background;

public abstract record AuditChainResult
{
    public sealed record Ok(long RowsChecked, long LastVerifiedId, byte[]? LastVerifiedHash) : AuditChainResult;
    public sealed record Broken(long FirstBadId, byte[] ExpectedHash, byte[] ActualHash) : AuditChainResult;
}
