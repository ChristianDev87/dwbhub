using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>auth_tokens</c> table for single-use
/// email-verification and password-reset tokens.
/// </summary>
[Migration(5, "Create auth_tokens table (email-verify + password-reset)")]
public sealed class Migration00005_AuthTokens : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("005_auth_tokens.sql");
    }
}
