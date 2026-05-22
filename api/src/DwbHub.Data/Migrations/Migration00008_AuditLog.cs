using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(8, "Create audit_log table with hash-chain + pgcrypto extension + autovacuum tuning")]
public sealed class Migration00008_AuditLog : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("008_audit_log.sql");
    }
}
