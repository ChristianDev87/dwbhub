using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(10, "Create audit_verify_state singleton + seed row")]
public sealed class Migration00010_AuditVerifyState : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("010_audit_verify_state.sql");
    }
}
