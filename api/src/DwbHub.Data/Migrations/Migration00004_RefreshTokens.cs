using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>refresh_tokens</c> table with single-use
/// rotation chain and role/is_active snapshot columns for drift detection.
/// </summary>
[Migration(4, "Create refresh_tokens table (rotation chain + role/is_active snapshot)")]
public sealed class Migration00004_RefreshTokens : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("004_refresh_tokens.sql");
    }
}
