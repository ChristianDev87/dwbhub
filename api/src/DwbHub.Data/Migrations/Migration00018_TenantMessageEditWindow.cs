using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Adds a nullable <c>message_edit_window_seconds</c> column to the <c>tenants</c>
/// table so each tenant can override the system-default 10-minute edit window.
/// NULL means "use system default"; 60..31536000 is an explicit per-tenant override.
/// </summary>
[Migration(18, "Add tenants.message_edit_window_seconds — per-tenant edit-window override")]
public sealed class Migration00018_TenantMessageEditWindow : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("018_tenant_message_edit_window.sql");
    }
}
