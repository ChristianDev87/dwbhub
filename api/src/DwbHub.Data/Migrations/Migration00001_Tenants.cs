using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(1, "Create tenants table — the root of every multi-tenant query")]
public sealed class Migration00001_Tenants : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("001_tenants.sql");
    }
}
