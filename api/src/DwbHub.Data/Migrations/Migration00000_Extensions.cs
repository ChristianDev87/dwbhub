using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Enables the <c>citext</c> PostgreSQL extension required for
/// case-insensitive text columns (tenant slug, user email).
/// </summary>
[Migration(0, "Enable required PostgreSQL extensions (citext)")]
public sealed class Migration00000_Extensions : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("000_extensions.sql");
    }
}
