using FluentMigrator;

namespace DwbHub.Data.Migrations;

[Migration(3, "Create login_attempt_log for lockout tracking (system-wide, allowlisted)")]
public sealed class Migration00003_LoginAttemptLog : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("003_login_attempt_log.sql");
    }
}
