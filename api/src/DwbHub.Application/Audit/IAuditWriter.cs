namespace DwbHub.Application.Audit;

public interface IAuditWriter
{
    /// <summary>
    /// Writes the event synchronously in the caller's transaction (uses the
    /// repo's connection; advisory lock serializes concurrent writers).
    /// Returns the auto-generated row id. Throws on DB error — callers MUST NOT
    /// swallow exceptions, because audit failure must abort the originating operation.
    /// </summary>
    Task<long> RecordAsync(AuditEvent evt, CancellationToken ct = default);
}
