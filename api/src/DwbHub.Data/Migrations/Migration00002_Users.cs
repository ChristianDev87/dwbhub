using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(2, "Create users table (per-tenant identity, BCrypt hash, role enum)")]
public sealed class Migration00002_Users : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("002_users.sql");
    }
}
