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
    private readonly ITenantRepository _tenants;
    private readonly IGuildRepository _guilds;

    /// <summary>
    /// Replaceable clock seam. Tests inject a fixed time; production uses UTC now.
    /// Matches the <c>_now</c> pattern used in <c>BotConnectionManager</c>.
    /// </summary>
    internal Func<DateTimeOffset> _now { get; set; } = () => DateTimeOffset.UtcNow;

    public MessageService(
        IMessageRepository messages,
        IGuildChannelRepository channels,
        IChannelWebhookRepository webhooks,
        IChannelWebhookCipher webhookCipher,
        IDiscordRestChannelClient discord,
        IAuditWriter audit,
        IMessagesBroadcaster broadcaster,
        ILogger<MessageService> logger,
        ITenantRepository tenants,
        IGuildRepository guilds)
    {
        _messages = messages;
        _channels = channels;
        _webhooks = webhooks;
        _webhookCipher = webhookCipher;
        _discord = discord;
        _audit = audit;
        _broadcaster = broadcaster;
        _logger = logger;
        _tenants = tenants;
        _guilds = guilds;
    }

    // ── Inbound ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
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
                ViaDwbhub: inserted.ViaDwbhub,
                DiscordMessageId: inserted.DiscordMessageId),
            channel.PublicId, ct).ConfigureAwait(false);

        return inserted;
    }

    // ── Edit ──────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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
            // TODO: Recreate the webhook here via ChannelWebhookService once that dependency is injectable.
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

    /// <inheritdoc/>
    public Task<IReadOnlyList<Message>> ListHistoryAsync(
        long tenantId,
        long channelId,
        long? beforeSnowflake,
        int limit,
        CancellationToken ct = default)
        => _messages.ListByChannelBeforeAsync(tenantId, channelId, beforeSnowflake, limit, ct);

    // ── EditAsync ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<EditMessageOutcome> EditAsync(
        long tenantId, long actorUserId, long messageId, string newContent,
        CancellationToken ct = default)
    {
        var trimmed = (newContent ?? string.Empty).Trim();
        if (trimmed.Length == 0) return new EditMessageOutcome.EmptyContent();
        if (trimmed.Length > 2000) return new EditMessageOutcome.ContentTooLong(trimmed.Length, 2000);

        var msg = await _messages.GetByInternalIdAsync(tenantId, messageId, ct).ConfigureAwait(false);
        if (msg is null) return new EditMessageOutcome.NotFound();
        if (msg.DeletedAt is not null) return new EditMessageOutcome.AlreadyDeleted();

        // Edit is for own-outbound only.
        if (!msg.ViaDwbhub || msg.DwbhubUserId != actorUserId)
            return new EditMessageOutcome.Forbidden();

        // Idempotent same-content no-op (no Discord call, no audit, no broadcast).
        if (msg.Content == trimmed)
            return new EditMessageOutcome.Success(msg);

        // Per-tenant edit window.
        var tenant = await _tenants.GetByIdAsync(tenantId, ct).ConfigureAwait(false);
        if (tenant?.MessageEditWindowSeconds is int window)
        {
            var age = (int)(_now() - msg.SentAt).TotalSeconds;
            if (age > window)
                return new EditMessageOutcome.EditWindowExpired(age, window);
        }

        // Look up webhook; decrypt token.
        var webhook = await _webhooks.GetByChannelAsync(tenantId, msg.ChannelId, ct).ConfigureAwait(false);
        if (webhook is null) return new EditMessageOutcome.DiscordError("webhook_not_found");

        var token = _webhookCipher.Decrypt(
            new ChannelWebhookEnvelope(webhook.Ciphertext, webhook.Nonce, webhook.AuthTag, webhook.KeyVersion));

        try
        {
            await _discord.EditWebhookMessageAsync(
                (ulong)webhook.DiscordWebhookId, token, (ulong)msg.DiscordMessageId, trimmed, ct)
                .ConfigureAwait(false);
        }
        catch (WebhookGoneException)
        {
            return new EditMessageOutcome.DiscordError("webhook_not_found");
        }
        catch (DiscordPermissionException)
        {
            return new EditMessageOutcome.DiscordError("webhook_patch_failed");
        }
        catch (DiscordRateLimitException)
        {
            return new EditMessageOutcome.DiscordError("discord_rate_limited");
        }

        var editedAt = _now();
        await _messages.UpdateContentAsync(tenantId, messageId, trimmed, editedAt, ct).ConfigureAwait(false);

        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: actorUserId,
            EventType: AuditEventTypes.MessageEditSelf,
            Payload: new Dictionary<string, object?>
            {
                ["messageId"] = messageId,
                ["channelId"] = msg.ChannelId,
                ["discordMessageId"] = msg.DiscordMessageId,
                ["previousContent"] = msg.Content,
                ["newContent"] = trimmed,
            }), ct).ConfigureAwait(false);

        var updated = msg with { Content = trimmed, EditedAt = editedAt };
        await _broadcaster.MessageUpdatedAsync(
            msg.TenantId, msg.DiscordMessageId, trimmed, editedAt, ct).ConfigureAwait(false);

        return new EditMessageOutcome.Success(updated);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DeleteMessageOutcome> DeleteAsync(
        long tenantId, long actorUserId, string actorRole, long messageId,
        CancellationToken ct = default)
    {
        var msg = await _messages.GetByInternalIdAsync(tenantId, messageId, ct).ConfigureAwait(false);
        if (msg is null) return new DeleteMessageOutcome.NotFound();
        if (msg.DeletedAt is not null) return new DeleteMessageOutcome.AlreadyDeleted();

        var isAuthor = msg.ViaDwbhub && msg.DwbhubUserId == actorUserId;
        var isOwner = string.Equals(actorRole, "Owner", StringComparison.Ordinal);

        string auditType;
        bool ok;

        if (isAuthor)
        {
            // Self-delete — even if caller is also Owner, author path produces a simpler audit trail.
            var webhook = await _webhooks.GetByChannelAsync(tenantId, msg.ChannelId, ct).ConfigureAwait(false);
            if (webhook is null) return new DeleteMessageOutcome.DiscordError("webhook_not_found");
            var token = _webhookCipher.Decrypt(
                new ChannelWebhookEnvelope(webhook.Ciphertext, webhook.Nonce, webhook.AuthTag, webhook.KeyVersion));
            ok = await _discord.DeleteWebhookMessageAsync(
                (ulong)webhook.DiscordWebhookId, token, (ulong)msg.DiscordMessageId, ct).ConfigureAwait(false);
            auditType = AuditEventTypes.MessageDeleteSelf;
        }
        else if (isOwner && msg.ViaDwbhub)
        {
            // Mod-outbound — same Discord API as self-delete, different audit.
            var webhook = await _webhooks.GetByChannelAsync(tenantId, msg.ChannelId, ct).ConfigureAwait(false);
            if (webhook is null) return new DeleteMessageOutcome.DiscordError("webhook_not_found");
            var token = _webhookCipher.Decrypt(
                new ChannelWebhookEnvelope(webhook.Ciphertext, webhook.Nonce, webhook.AuthTag, webhook.KeyVersion));
            ok = await _discord.DeleteWebhookMessageAsync(
                (ulong)webhook.DiscordWebhookId, token, (ulong)msg.DiscordMessageId, ct).ConfigureAwait(false);
            auditType = AuditEventTypes.MessageDeleteModerationOutbound;
        }
        else if (isOwner && !msg.ViaDwbhub)
        {
            // Mod-inbound — requires bot MANAGE_MESSAGES permission; uses bot REST API.
            // Resolve channel by internal id → guild → permission check.
            var bridged = await _channels.ListBridgedAsync(tenantId, ct).ConfigureAwait(false);
            var chan = bridged.FirstOrDefault(c => c.Id == msg.ChannelId);
            if (chan is null) return new DeleteMessageOutcome.NotFound();

            var guild = await _guilds.GetByIdAsync(chan.GuildId, ct).ConfigureAwait(false);
            if (guild?.TenantId != tenantId) return new DeleteMessageOutcome.NotFound();
            if (guild.BotCanManageMessages != true)
                return new DeleteMessageOutcome.BotMissingPermission();

            ok = await _discord.DeleteChannelMessageAsync(
                (ulong)chan.DiscordChannelId, (ulong)msg.DiscordMessageId, guild.Id, ct).ConfigureAwait(false);
            if (!ok)
            {
                // Reactive cache update: Discord said no — mark permission as false for future requests.
                await _guilds.UpdateBotPermissionsAsync(
                    guild.Id, tenantId, canManageMessages: false, ct).ConfigureAwait(false);
                return new DeleteMessageOutcome.BotMissingPermission();
            }
            auditType = AuditEventTypes.MessageDeleteModerationInbound;
        }
        else
        {
            return new DeleteMessageOutcome.Forbidden();
        }

        if (!ok) return new DeleteMessageOutcome.DiscordError("discord_delete_failed");

        var deletedAt = _now();
        await _messages.SoftDeleteByIdAsync(tenantId, messageId, deletedAt, ct).ConfigureAwait(false);

        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: actorUserId,
            EventType: auditType,
            Payload: new Dictionary<string, object?>
            {
                ["messageId"] = messageId,
                ["channelId"] = msg.ChannelId,
                ["discordMessageId"] = msg.DiscordMessageId,
                ["originalAuthorUserId"] = msg.DwbhubUserId,
                ["originalAuthorDiscordId"] = msg.ViaDwbhub ? null : (long?)msg.DiscordAuthorId,
            }), ct).ConfigureAwait(false);

        await _broadcaster.MessageDeletedAsync(msg.TenantId, msg.DiscordMessageId, ct).ConfigureAwait(false);

        return new DeleteMessageOutcome.Success();
    }

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
                ViaDwbhub: inserted.ViaDwbhub,
                DiscordMessageId: inserted.DiscordMessageId),
            channel.PublicId, ct).ConfigureAwait(false);

        return inserted;
    }
}
