using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(7, "Add tenants.locale (de/en, default de)")]
public sealed class Migration00007_TenantsLocale : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("007_tenants_locale.sql");
    }
}
