using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(13, "Create guild_channels table (Discord channel registry + bridge-toggle, Plan 1.0)")]
public sealed class Migration00013_GuildChannels : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("013_guild_channels.sql");
    }
}
