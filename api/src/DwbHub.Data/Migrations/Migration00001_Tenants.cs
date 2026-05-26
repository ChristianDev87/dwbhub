using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>tenants</c> table, the root of every
/// multi-tenant query in DwbHub.
/// </summary>
[Migration(1, "Create tenants table — the root of every multi-tenant query")]
public sealed class Migration00001_Tenants : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("001_tenants.sql");
    }
}
