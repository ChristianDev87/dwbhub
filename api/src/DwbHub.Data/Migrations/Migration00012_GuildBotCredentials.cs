using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>guild_bot_credentials</c> table for storing
/// AES-256-GCM encrypted Discord bot tokens at rest.
/// </summary>
[Migration(12, "Create guild_bot_credentials table (AES-256-GCM at-rest encryption, Plan 0.7)")]
public sealed class Migration00012_GuildBotCredentials : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("012_guild_bot_credentials.sql");
    }
}
