using DwbHub.Application.Audit;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace DwbHub.Application.Messaging;

/// <summary>
/// Orchestrates Discord message lifecycle: inbound persistence, edits, soft-deletes,
/// and outbound sending via per-channel webhooks.
///
/// Inbound filter logic (deferred from Task 5 DiscordNetBotConnection):
///   1. Bridged-channel gate  — skip if channel unknown or not is_bridged
///   2. DM-channel safety net  — skip if channel_type == 1 (DM)
///   3. Webhook-loop prevention — skip if author is our own registered webhook
/// </summary>
public sealed class MessageService : IMessageService
{
    // Discord channel type 1 = DM. Guard against mis-routed events.
    private const short DmChannelType = 1;

    private readonly IMessageRepository _messages;
    private readonly IGuildChannelRepository _channels;
    private readonly IChannelWebhookRepository _webhooks;
    private readonly IChannelWebhookCipher _webhookCipher;
    private readonly IDiscordRestChannelClient _discord;
    private readonly IAuditWriter _audit;
    private readonly IMessagesBroadcaster _broadcaster;
    private readonly ILogger<MessageService> _logger;

    public MessageService(
        IMessageRepository messages,
        IGuildChannelRepository channels,
        IChannelWebhookRepository webhooks,
        IChannelWebhookCipher webhookCipher,
        IDiscordRestChannelClient discord,
        IAuditWriter audit,
        IMessagesBroadcaster broadcaster,
        ILogger<MessageService> logger)
    {
        _messages = messages;
        _channels = channels;
        _webhooks = webhooks;
        _webhookCipher = webhookCipher;
        _discord = discord;
        _audit = audit;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    // ── Inbound ───────────────────────────────────────────────────────────────

    public async Task<Message?> PersistInboundAsync(MessageReceivedEvent evt, CancellationToken ct = default)
    {
        // 1. Resolve channel — skip if unknown or not bridged
        var channel = await _channels.GetByDiscordIdAsync(evt.TenantId, evt.DiscordChannelId, ct)
            .ConfigureAwait(false);
        if (channel is null || !channel.IsBridged)
            return null;

        // 2. DM-channel safety net
        if (channel.ChannelType == DmChannelType)
        {
            _logger.LogWarning(
                "Inbound message on DM-type channel {ChannelId} tenant {TenantId} — suppressed",
                evt.DiscordChannelId, evt.TenantId);
            return null;
        }

        // 3. Webhook-loop prevention: skip if author is our own Discord webhook
        if (evt.AuthorIsWebhook && evt.WebhookSourceId is { } srcId)
        {
            var ourWebhook = await _webhooks.GetByChannelAsync(evt.TenantId, channel.Id, ct)
                .ConfigureAwait(false);
            if (ourWebhook is not null && (ulong)ourWebhook.DiscordWebhookId == srcId)
            {
                _logger.LogDebug(
                    "Webhook-loop suppressed channel {ChannelId} webhook {WebhookId}",
                    channel.Id, srcId);
                return null;
            }
        }

        var msg = new Message
        {
            TenantId = evt.TenantId,
            ChannelId = channel.Id,
            DiscordMessageId = evt.DiscordMessageId,
            DiscordAuthorId = evt.DiscordAuthorId,
            DiscordAuthorName = evt.DiscordAuthorName,
            ViaDwbhub = false,
            Content = evt.Content,
            SentAt = evt.SentAt,
        };

        var inserted = await _messages.InsertAsync(msg, ct).ConfigureAwait(false);
        if (inserted is null)
            return null; // duplicate (backfill / gateway race) — already persisted, skip broadcast

        await _audit.RecordAsync(new AuditEvent(
            TenantId: evt.TenantId,
            ActorUserId: null,
            EventType: AuditEventTypes.MessageReceivedInbound,
            Payload: new Dictionary<string, object?>
            {
                ["channel_id"] = channel.Id,
                ["discord_message_id"] = inserted.DiscordMessageId,
                ["discord_author_id"] = inserted.DiscordAuthorId,
            }), ct).ConfigureAwait(false);

        await _broadcaster.MessageReceivedAsync(
            new MessageBroadcastDto(
                Id: inserted.Id,
                TenantId: inserted.TenantId,
                ChannelPublicId: channel.PublicId,
                AuthorName: inserted.DiscordAuthorName,
                Content: inserted.Content,
                SentAt: inserted.SentAt,
                ViaDwbhub: inserted.ViaDwbhub),
            channel.PublicId, ct).ConfigureAwait(false);

        return inserted;
    }

    // ── Edit ──────────────────────────────────────────────────────────────────

    public async Task PersistEditAsync(MessageUpdatedEvent evt, CancellationToken ct = default)
    {
        var updated = await _messages.ApplyEditAsync(
            evt.TenantId, evt.DiscordMessageId, evt.Content, evt.EditedAt, ct)
            .ConfigureAwait(false);
        if (!updated)
            return; // message not in DB (private channel / never synced) — silent

        await _audit.RecordAsync(new AuditEvent(
            TenantId: evt.TenantId,
            ActorUserId: null,
            EventType: AuditEventTypes.MessageEdited,
            Payload: new Dictionary<string, object?>
            {
                ["discord_message_id"] = evt.DiscordMessageId,
            }), ct).ConfigureAwait(false);

        await _broadcaster.MessageUpdatedAsync(
            evt.TenantId, evt.DiscordMessageId, evt.Content, evt.EditedAt, ct)
            .ConfigureAwait(false);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task MarkDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default)
    {
        var deleted = await _messages.MarkDeletedAsync(
            evt.TenantId, evt.DiscordMessageId, ct)
            .ConfigureAwait(false);
        if (!deleted)
            return;

        await _audit.RecordAsync(new AuditEvent(
            TenantId: evt.TenantId,
            ActorUserId: null,
            EventType: AuditEventTypes.MessageDeleted,
            Payload: new Dictionary<string, object?>
            {
                ["discord_message_id"] = evt.DiscordMessageId,
            }), ct).ConfigureAwait(false);

        await _broadcaster.MessageDeletedAsync(evt.TenantId, evt.DiscordMessageId, ct)
            .ConfigureAwait(false);
    }

    // ── Outbound ──────────────────────────────────────────────────────────────

    public async Task<Message> SendOutboundAsync(
        long tenantId,
        long channelId,
        long userId,
        string displayName,
        string content,
        CancellationToken ct = default)
    {
        var channel = await ResolveChannelByIdAsync(tenantId, channelId, ct)
            .ConfigureAwait(false);

        var webhookRow = await _webhooks.GetByChannelAsync(tenantId, channelId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No webhook for channel {channelId} (tenant {tenantId}). Enable the bridge first.");

        var token = _webhookCipher.Decrypt(
            new ChannelWebhookEnvelope(
                webhookRow.Ciphertext,
                webhookRow.Nonce,
                webhookRow.AuthTag,
                webhookRow.KeyVersion));

        DiscordMessageInfo sentInfo;
        try
        {
            sentInfo = await _discord.ExecuteWebhookAsync(
                (ulong)webhookRow.DiscordWebhookId, token, displayName, content, ct)
                .ConfigureAwait(false);
        }
        catch (WebhookGoneException)
        {
            // Discord 404 — recreate webhook and retry once.
            // Full recreation requires bot token (held by ChannelWebhookService in Task 10).
            // For now we surface a clear exception so Task 10 can wrap this method.
            _logger.LogWarning(
                "Webhook {WebhookId} gone (404) for channel {ChannelId} tenant {TenantId}; recreation required",
                webhookRow.DiscordWebhookId, channelId, tenantId);
            throw new InvalidOperationException(
                $"Discord webhook {webhookRow.DiscordWebhookId} not found. Recreate via ChannelWebhookService.");
        }

        return await PersistOutboundAsync(
            tenantId, channelId, userId, displayName, content, channel, sentInfo, ct)
            .ConfigureAwait(false);
    }

    // ── History ───────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<Message>> ListHistoryAsync(
        long tenantId,
        long channelId,
        long? beforeSnowflake,
        int limit,
        CancellationToken ct = default)
        => _messages.ListByChannelBeforeAsync(tenantId, channelId, beforeSnowflake, limit, ct);

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<GuildChannel> ResolveChannelByIdAsync(
        long tenantId, long channelId, CancellationToken ct)
    {
        // IGuildChannelRepository has no GetByInternalIdAsync; use bridged list.
        // Bridged channels are few so this is acceptable.
        var bridged = await _channels.ListBridgedAsync(tenantId, ct).ConfigureAwait(false);
        return bridged.FirstOrDefault(c => c.Id == channelId)
            ?? throw new InvalidOperationException(
                $"Bridged channel {channelId} not found for tenant {tenantId}.");
    }

    private async Task<Message> PersistOutboundAsync(
        long tenantId,
        long channelId,
        long userId,
        string displayName,
        string content,
        GuildChannel channel,
        DiscordMessageInfo sentInfo,
        CancellationToken ct)
    {
        var msg = new Message
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordMessageId = (long)sentInfo.Id,
            DiscordAuthorId = (long)sentInfo.AuthorId,
            DiscordAuthorName = displayName,
            ViaDwbhub = true,
            DwbhubUserId = userId,
            Content = content,
            SentAt = sentInfo.SentAt,
        };

        var inserted = await _messages.InsertAsync(msg, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Duplicate snowflake {msg.DiscordMessageId} on outbound insert — unexpected.");

        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: userId,
            EventType: AuditEventTypes.MessageSentOutbound,
            Payload: new Dictionary<string, object?>
            {
                ["channel_id"] = channelId,
                ["discord_message_id"] = inserted.DiscordMessageId,
                ["via_dwbhub"] = true,
            }), ct).ConfigureAwait(false);

        await _broadcaster.MessageReceivedAsync(
            new MessageBroadcastDto(
                Id: inserted.Id,
                TenantId: inserted.TenantId,
                ChannelPublicId: channel.PublicId,
                AuthorName: inserted.DiscordAuthorName,
                Content: inserted.Content,
                SentAt: inserted.SentAt,
                ViaDwbhub: inserted.ViaDwbhub),
            channel.PublicId, ct).ConfigureAwait(false);

        return inserted;
    }
}
