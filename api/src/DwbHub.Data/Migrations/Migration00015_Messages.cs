using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(15, "Create messages table (Discord message persistence + soft-delete + snowflake dedup, Plan 1.0)")]
public sealed class Migration00015_Messages : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("015_messages.sql");
    }
}
