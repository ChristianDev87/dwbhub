using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(11, "Create guilds table (multi-guild schema, Plan 0.6)")]
public sealed class Migration00011_Guilds : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("011_guilds.sql");
    }
}
