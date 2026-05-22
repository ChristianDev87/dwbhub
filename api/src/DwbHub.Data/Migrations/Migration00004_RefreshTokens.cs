using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(4, "Create refresh_tokens table (rotation chain + role/is_active snapshot)")]
public sealed class Migration00004_RefreshTokens : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("004_refresh_tokens.sql");
    }
}
