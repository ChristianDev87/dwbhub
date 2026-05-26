using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>audit_log</c> table with SHA-256 hash-chain
/// integrity, enables the <c>pgcrypto</c> extension, and tunes autovacuum
/// for append-only workload.
/// </summary>
[Migration(8, "Create audit_log table with hash-chain + pgcrypto extension + autovacuum tuning")]
public sealed class Migration00008_AuditLog : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("008_audit_log.sql");
    }
}
