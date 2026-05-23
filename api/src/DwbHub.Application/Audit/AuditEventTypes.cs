namespace DwbHub.Application.Audit;

/// <summary>
/// Centralised constants for well-known audit event type strings.
/// Alphabetical by constant name within each domain group.
/// </summary>
public static class AuditEventTypes
{
    public const string BotManualReconnect = "bot.manual_reconnect";
    public const string GuildActivated     = "guild.activated";
    public const string GuildDeactivated   = "guild.deactivated";
}
