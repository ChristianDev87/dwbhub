using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Adds the <c>message_edit_window_seconds</c> column to <c>tenants</c>.
/// NULL = unlimited; otherwise seconds (1 min to 1 year), Plan 1.1.
/// </summary>
[Migration(17, "Plan 1.1: per-tenant message edit window")]
public sealed class Migration00017_TenantMessageEditWindow : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("017_tenant_message_edit_window.sql");
    }
}
