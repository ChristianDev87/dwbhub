using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(12, "Create guild_bot_credentials table (AES-256-GCM at-rest encryption, Plan 0.7)")]
public sealed class Migration00012_GuildBotCredentials : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("012_guild_bot_credentials.sql");
    }
}
