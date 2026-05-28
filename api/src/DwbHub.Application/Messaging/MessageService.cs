using DwbHub.Application.Audit;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
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
    private readonly IGuildBotCredentialRepository _botCredentials;
    private readonly IBotTokenEncryptor _encryptor;
    private readonly IDiscordRestChannelClient _discord;
    private readonly IAuditWriter _audit;
    private readonly IMessagesBroadcaster _broadcaster;
    private readonly ILogger<MessageService> _logger;
    private readonly ITenantRepository _tenants;

    public MessageService(
        IMessageRepository messages,
        IGuildChannelRepository channels,
        IChannelWebhookRepository webhooks,
        IChannelWebhookCipher webhookCipher,
        IGuildBotCredentialRepository botCredentials,
        IBotTokenEncryptor encryptor,
        IDiscordRestChannelClient discord,
        IAuditWriter audit,
        IMessagesBroadcaster broadcaster,
        ILogger<MessageService> logger,
        ITenantRepository tenants)
    {
        _messages = messages;
        _channels = channels;
        _webhooks = webhooks;
        _webhookCipher = webhookCipher;
        _botCredentials = botCredentials;
        _encryptor = encryptor;
        _discord = discord;
        _audit = audit;
        _broadcaster = broadcaster;
        _logger = logger;
        _tenants = tenants;
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

    // ── User-facing Edit ──────────────────────────────────────────────────────

    private static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(10);

    /// <inheritdoc/>
    public async Task<EditMessageResult> EditOutboundAsync(
        long tenantId,
        long channelId,
        Guid messagePublicId,
        long actorUserId,
        string newContent,
        CancellationToken ct = default)
    {
        var message = await _messages.GetByPublicIdAsync(tenantId, channelId, messagePublicId, ct)
            .ConfigureAwait(false);

        if (message is null || message.DeletedAt is not null)
            return new EditMessageResult.NotFound();

        // Only the original author may edit (via-dwbhub messages only — author check).
        if (message.DwbhubUserId != actorUserId)
            return new EditMessageResult.Forbidden();

        // Per-tenant override falls back to the system default.
        var tenant = await _tenants.GetByIdAsync(tenantId, ct).ConfigureAwait(false);
        var window = tenant?.MessageEditWindowSeconds is int s
            ? TimeSpan.FromSeconds(s)
            : EditWindow;
        var age = DateTimeOffset.UtcNow - message.SentAt;
        if (age > window)
            return new EditMessageResult.EditWindowExpired();

        // Resolve webhook for this channel to call Discord.
        var webhookRow = await _webhooks.GetByChannelAsync(tenantId, channelId, ct)
            .ConfigureAwait(false);
        if (webhookRow is null)
        {
            // No webhook means the message was never sent via our webhook — cannot edit.
            return new EditMessageResult.Forbidden();
        }

        var token = _webhookCipher.Decrypt(
            new ChannelWebhookEnvelope(
                webhookRow.Ciphertext,
                webhookRow.Nonce,
                webhookRow.AuthTag,
                webhookRow.KeyVersion));

        // Push to Discord. WebhookGoneException (404) maps to edit_window_expired per spec.
        try
        {
            await _discord.EditWebhookMessageAsync(
                (ulong)webhookRow.DiscordWebhookId,
                token,
                (ulong)message.DiscordMessageId,
                newContent,
                ct).ConfigureAwait(false);
        }
        catch (WebhookGoneException)
        {
            return new EditMessageResult.EditWindowExpired();
        }

        var editedAt = DateTimeOffset.UtcNow;
        await _messages.ApplyEditAsync(tenantId, message.DiscordMessageId, newContent, editedAt, ct)
            .ConfigureAwait(false);

        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: actorUserId,
            EventType: AuditEventTypes.MessageEdited,
            Payload: new Dictionary<string, object?>
            {
                ["channel_id"] = channelId,
                ["discord_message_id"] = message.DiscordMessageId,
                ["message_public_id"] = messagePublicId,
                // Intentionally NO "content" — keep PII out of the audit trail.
            }), ct).ConfigureAwait(false);

        await _broadcaster.MessageUpdatedAsync(
            tenantId, message.DiscordMessageId, newContent, editedAt, ct)
            .ConfigureAwait(false);

        // Return the updated message projection.
        var updated = message with { Content = newContent, EditedAt = editedAt };
        return new EditMessageResult.Success(updated);
    }

    // ── User-facing Delete ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DeleteMessageResult> DeleteOutboundAsync(
        long tenantId,
        long channelId,
        Guid messagePublicId,
        long actorUserId,
        UserRole actorRole,
        CancellationToken ct = default)
    {
        var message = await _messages.GetByPublicIdAsync(tenantId, channelId, messagePublicId, ct)
            .ConfigureAwait(false);

        if (message is null || message.DeletedAt is not null)
            return new DeleteMessageResult.NotFound();

        // Author OR tenant Owner may delete.
        var isAuthor = message.DwbhubUserId == actorUserId;
        var isOwner = actorRole == UserRole.Owner;
        if (!isAuthor && !isOwner)
            return new DeleteMessageResult.Forbidden();

        // Hard-delete from Discord. Path depends on message origin:
        //   ViaDwbhub=true  → posted by our webhook → use webhook token (existing path)
        //   ViaDwbhub=false → inbound from gateway  → must use bot token for moderation delete
        var channel = await ResolveChannelByIdAsync(tenantId, channelId, ct)
            .ConfigureAwait(false);

        if (message.ViaDwbhub)
        {
            // Existing outbound webhook delete path — unverändert.
            var webhookRow = await _webhooks.GetByChannelAsync(tenantId, channelId, ct)
                .ConfigureAwait(false);
            if (webhookRow is not null)
            {
                var token = _webhookCipher.Decrypt(
                    new ChannelWebhookEnvelope(
                        webhookRow.Ciphertext,
                        webhookRow.Nonce,
                        webhookRow.AuthTag,
                        webhookRow.KeyVersion));

                try
                {
                    await _discord.DeleteWebhookMessageAsync(
                        (ulong)webhookRow.DiscordWebhookId,
                        token,
                        (ulong)message.DiscordMessageId,
                        ct).ConfigureAwait(false);
                }
                catch (WebhookGoneException)
                {
                    // Webhook gone — still proceed with local soft-delete.
                    _logger.LogWarning(
                        "Webhook gone when trying to delete message {DiscordMessageId} for tenant {TenantId}; proceeding with local soft-delete",
                        message.DiscordMessageId, tenantId);
                }
            }
        }
        else
        {
            // Inbound message (ViaDwbhub=false): use the bot token for a moderation delete.
            // The bot needs MANAGE_MESSAGES on the channel.
            // Production setup note: the test server grants admin to all bots (MANAGE_MESSAGES
            // included); production channel permissions must explicitly grant MANAGE_MESSAGES
            // to the bot role, otherwise Discord returns 403.
            var cred = await _botCredentials.GetByGuildIdAsync(channel.GuildId, tenantId, ct)
                .ConfigureAwait(false);

            if (cred is null)
            {
                _logger.LogWarning(
                    "Cannot moderation-delete inbound message {MessageId} (discord_id={DiscordMessageId}): " +
                    "no bot credentials for guild {GuildId}; proceeding with DB soft-delete only",
                    message.Id, message.DiscordMessageId, channel.GuildId);
            }
            else
            {
                var botToken = _encryptor.Decrypt(new CipherEnvelope(cred.Nonce, cred.Ciphertext, cred.Tag));
                try
                {
                    await _discord.DeleteMessageAsync(
                        channelId: (ulong)channel.DiscordChannelId,
                        messageId: (ulong)message.DiscordMessageId,
                        botToken: botToken,
                        ct: ct).ConfigureAwait(false);
                }
                catch (DiscordPermissionException ex)
                {
                    // 401/403: bot lacks MANAGE_MESSAGES on this channel, or token invalid.
                    // Soft-delete proceeds locally — Discord state may diverge.
                    _logger.LogWarning(ex,
                        "Discord moderation delete refused (permission/auth) for inbound message {MessageId} " +
                        "(discord_id={DiscordMessageId}); proceeding with DB soft-delete only. " +
                        "Ensure the bot has MANAGE_MESSAGES on this channel.",
                        message.Id, message.DiscordMessageId);
                }
                catch (DiscordRateLimitException)
                {
                    // Rate-limited — re-throw so the caller (HTTP request pipeline) can surface
                    // a 429 to the client and let it retry. Do not soft-delete yet.
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    // Transient network failure. Log at Error so it is visible in CI and
                    // test output rather than silently swallowed. Soft-delete proceeds.
                    _logger.LogError(ex,
                        "Discord moderation delete network failure for inbound message {MessageId} " +
                        "(discord_id={DiscordMessageId}); proceeding with DB soft-delete only.",
                        message.Id, message.DiscordMessageId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Unexpected exception — log at Error (not Warning) so it surfaces in
                    // test logs and CI, making the root cause visible rather than hidden.
                    // OperationCanceledException is let through (request cancelled / timeout).
                    _logger.LogError(ex,
                        "Discord moderation delete unexpected error for inbound message {MessageId} " +
                        "(discord_id={DiscordMessageId}); proceeding with DB soft-delete only.",
                        message.Id, message.DiscordMessageId);
                }
                // botToken eligible for GC here — never stored, cached, or logged.
            }
        }

        // Soft-delete locally (content preserved for audit trail).
        await _messages.MarkDeletedAsync(tenantId, message.DiscordMessageId, ct)
            .ConfigureAwait(false);

        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: actorUserId,
            EventType: AuditEventTypes.MessageDeleted,
            Payload: new Dictionary<string, object?>
            {
                ["channel_id"] = channelId,
                ["discord_message_id"] = message.DiscordMessageId,
                ["message_public_id"] = messagePublicId,
                // Intentionally NO "content" — keep PII out of the audit trail.
            }), ct).ConfigureAwait(false);

        await _broadcaster.MessageDeletedAsync(tenantId, message.DiscordMessageId, ct)
            .ConfigureAwait(false);

        return new DeleteMessageResult.Success();
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
