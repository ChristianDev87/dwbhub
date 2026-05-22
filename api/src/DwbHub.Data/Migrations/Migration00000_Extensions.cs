using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(0, "Enable required PostgreSQL extensions (citext)")]
public sealed class Migration00000_Extensions : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("000_extensions.sql");
    }
}
