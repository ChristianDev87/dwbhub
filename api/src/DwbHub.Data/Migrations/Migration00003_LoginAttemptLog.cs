using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>login_attempt_log</c> table for per-IP
/// failed-login tracking used by the rate-limit gate in LoginService.
/// </summary>
[Migration(3, "Create login_attempt_log for lockout tracking (system-wide, allowlisted)")]
public sealed class Migration00003_LoginAttemptLog : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("003_login_attempt_log.sql");
    }
}
