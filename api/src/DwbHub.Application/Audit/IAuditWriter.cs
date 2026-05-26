namespace DwbHub.Application.Audit;

/// <summary>
/// Writes security-audit events to the immutable audit log with SHA-256 hash-chain integrity.
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Writes the event synchronously in the caller's transaction (uses the
    /// repo's connection; advisory lock serializes concurrent writers).
    /// Returns the auto-generated row id.
    /// Throws on DB error — callers should let the exception propagate; swallowing it would silently lose the audit record.
    /// </summary>
    Task<long> RecordAsync(AuditEvent evt, CancellationToken ct = default);
}
