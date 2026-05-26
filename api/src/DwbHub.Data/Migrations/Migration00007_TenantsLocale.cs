using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Adds the <c>locale</c> column to <c>tenants</c> for BCP 47
/// language preference (default <c>de</c>).
/// </summary>
[Migration(7, "Add tenants.locale (de/en, default de)")]
public sealed class Migration00007_TenantsLocale : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("007_tenants_locale.sql");
    }
}
