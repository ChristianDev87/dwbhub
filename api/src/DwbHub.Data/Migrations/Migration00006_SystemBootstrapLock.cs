using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>system_bootstrap_lock</c> singleton table
/// that gates the first-time setup wizard.
/// </summary>
[Migration(6, "Create system_bootstrap_lock (first-time setup wizard gate)")]
public sealed class Migration00006_SystemBootstrapLock : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("006_system_bootstrap_lock.sql");
    }
}
