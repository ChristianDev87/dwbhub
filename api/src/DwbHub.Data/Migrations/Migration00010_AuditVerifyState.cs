using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>audit_verify_state</c> singleton table and
/// inserts the initial seed row used by the chain-integrity background job.
/// </summary>
[Migration(10, "Create audit_verify_state singleton + seed row")]
public sealed class Migration00010_AuditVerifyState : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("010_audit_verify_state.sql");
    }
}
