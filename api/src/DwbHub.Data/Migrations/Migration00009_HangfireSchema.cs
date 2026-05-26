using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>hangfire</c> schema so Hangfire can bootstrap
/// its own tables on first start without requiring superuser privileges.
/// </summary>
[Migration(9, "Create hangfire schema (Hangfire bootstraps its own tables on first start)")]
public sealed class Migration00009_HangfireSchema : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("009_hangfire_schema.sql");
    }
}
