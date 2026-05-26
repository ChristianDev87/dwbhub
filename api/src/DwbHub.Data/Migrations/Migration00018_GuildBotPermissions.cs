using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Adds the <c>bot_can_manage_messages</c> column to <c>guilds</c>.
/// Tri-state: NULL = not yet checked, FALSE = lacks, TRUE = has MANAGE_MESSAGES. Plan 1.1.
/// </summary>
[Migration(18, "Plan 1.1: bot MANAGE_MESSAGES permission cache")]
public sealed class Migration00018_GuildBotPermissions : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("018_guild_bot_permissions.sql");
    }
}
