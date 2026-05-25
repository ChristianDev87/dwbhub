namespace DwbHub.Application.Audit;

/// <summary>
/// Centralised constants for well-known audit event type strings.
/// Alphabetical by constant name within each domain group.
/// </summary>
public static class AuditEventTypes
{
    public const string BotManualReconnect = "bot.manual_reconnect";
    public const string GuildActivated = "guild.activated";
    public const string GuildDeactivated = "guild.deactivated";

    // Messaging events (Plan 1.0)
    public const string ChannelSyncCompleted = "channel.sync_completed";
    public const string MessageDeleted = "message.deleted";
    public const string MessageEdited = "message.edited";
    public const string MessageReceivedInbound = "message.received_inbound";
    public const string MessageSentOutbound = "message.sent_outbound";
}
