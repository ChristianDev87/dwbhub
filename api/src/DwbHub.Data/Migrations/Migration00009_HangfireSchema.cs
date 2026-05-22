using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(9, "Create hangfire schema (Hangfire bootstraps its own tables on first start)")]
public sealed class Migration00009_HangfireSchema : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("009_hangfire_schema.sql");
    }
}
