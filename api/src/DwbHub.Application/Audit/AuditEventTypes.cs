namespace DwbHub.Application.Audit;

/// <summary>
/// Centralised constants for well-known audit event type strings.
/// Alphabetical by constant name within each domain group.
/// </summary>
public static class AuditEventTypes
{
    public const string BotManualReconnect = "bot.manual_reconnect";
    public const string BotManualReconnectThrottled = "bot.manual_reconnect_throttled";
    public const string GuildActivated = "guild.activated";
    public const string GuildDeactivated = "guild.deactivated";
    public const string ChannelBackfillCancelled = "channel.backfill_cancelled";
    public const string ChannelBackfillCompleted = "channel.backfill_completed";
    public const string ChannelBackfillFailed = "channel.backfill_failed";
    public const string ChannelBackfillStarted = "channel.backfill_started";
    public const string ChannelBridged = "channel.bridged";
    public const string ChannelSyncCompleted = "channel.sync_completed";
    public const string ChannelSyncRequested = "channel.sync_requested";
    public const string ChannelUnbridged = "channel.unbridged";
    public const string ChannelWebhookCreated = "channel.webhook_created";
    public const string ChannelWebhookDeleted = "channel.webhook_deleted";
    public const string MessageDeleted = "message.deleted";
    public const string MessageEdited = "message.edited";
    public const string MessageReceivedInbound = "message.received_inbound";
    public const string MessageSentOutbound = "message.sent_outbound";
}
