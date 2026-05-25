using DwbHub.Application.Audit;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace DwbHub.Application.Messaging;

/// <summary>
/// Owns the Discord webhook credential lifecycle for bridged channels.
///
/// Security invariant: the raw webhook token is never logged, never placed in
/// an error message, and never returned to any HTTP caller. It lives in memory
/// only during the outbound Discord REST call and is immediately discarded.
///
/// Note on channel resolution: <see cref="IGuildChannelRepository"/> does not
/// expose a GetByInternalIdAsync. To find the GuildId for a channel (required
/// when looking up bot credentials), this service enumerates the guilds for the
/// tenant and scans channels per guild. This is N+1 but N is bounded (guilds ≤
/// tens, channels per guild ≤ hundreds) and bridge operations are not hot-path.
/// A GetByInternalIdAsync can be added in a future task to avoid this.
/// </summary>
public sealed class ChannelWebhookService : IChannelWebhookService
{
    private readonly IChannelWebhookRepository _repo;
    private readonly IChannelWebhookCipher _cipher;
    private readonly IDiscordRestChannelClient _discord;
    private readonly IGuildChannelRepository _channels;
    private readonly IGuildRepository _guilds;
    private readonly IGuildBotCredentialRepository _creds;
    private readonly IBotTokenEncryptor _botCipher;
    private readonly IAuditWriter _audit;
    private readonly ILogger<ChannelWebhookService> _logger;

    public ChannelWebhookService(
        IChannelWebhookRepository repo,
        IChannelWebhookCipher cipher,
        IDiscordRestChannelClient discord,
        IGuildChannelRepository channels,
        IGuildRepository guilds,
        IGuildBotCredentialRepository creds,
        IBotTokenEncryptor botCipher,
        IAuditWriter audit,
        ILogger<ChannelWebhookService> logger)
    {
        _repo = repo;
        _cipher = cipher;
        _discord = discord;
        _channels = channels;
        _guilds = guilds;
        _creds = creds;
        _botCipher = botCipher;
        _audit = audit;
        _logger = logger;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task CreateForChannelAsync(
        long tenantId,
        long channelId,
        long userId,
        CancellationToken ct = default)
    {
        // 1. Resolve channel + decrypt bot token.
        var (channel, botToken) = await ResolveChannelAndBotTokenAsync(
            tenantId, channelId, ct).ConfigureAwait(false);

        // 2. Call Discord REST API — create the webhook.
        //    Token is in memory only during this call.
        var created = await _discord.CreateWebhookAsync(
            botToken,
            discordChannelId: (ulong)channel.DiscordChannelId,
            name: "DwbHub",
            ct).ConfigureAwait(false);

        // 3. Encrypt token immediately; raw token leaves this scope after Encrypt().
        var envelope = _cipher.Encrypt(created.WebhookToken);

        // 4. INSERT channel_webhooks.
        //    On insert failure → rollback by deleting the Discord webhook to prevent orphan.
        try
        {
            var webhookRow = new ChannelWebhook
            {
                TenantId = tenantId,
                ChannelId = channelId,
                DiscordWebhookId = (long)created.WebhookId,
                Ciphertext = envelope.Ciphertext,
                Nonce = envelope.Nonce,
                AuthTag = envelope.AuthTag,
                KeyVersion = envelope.KeyVersion,
                CreatedByUserId = userId,
            };
            await _repo.InsertAsync(webhookRow, ct).ConfigureAwait(false);
        }
        catch (Exception insertEx)
        {
            // Rollback: delete the Discord webhook to prevent an orphan credential.
            _logger.LogError(insertEx,
                "Failed to insert channel_webhook row for channel {ChannelId} tenant {TenantId}. " +
                "Attempting Discord webhook rollback for webhook {WebhookId}.",
                channelId, tenantId, created.WebhookId);
            try
            {
                await _discord.DeleteWebhookAsync(
                    created.WebhookId,
                    created.WebhookToken,
                    CancellationToken.None).ConfigureAwait(false);

                await _audit.RecordAsync(new AuditEvent(
                    TenantId: tenantId,
                    ActorUserId: userId,
                    EventType: AuditEventTypes.ChannelWebhookDeleted,
                    Payload: new Dictionary<string, object?>
                    {
                        ["channel_id"] = channelId,
                        ["discord_webhook_id"] = (long)created.WebhookId,
                        ["reason"] = "rollback_on_db_insert_failure",
                    }), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                // Log without the webhook token — webhook ID only.
                _logger.LogError(rollbackEx,
                    "Discord webhook rollback failed: webhook {WebhookId} may be orphaned " +
                    "in Discord for tenant {TenantId} channel {ChannelId}. Manual cleanup required.",
                    created.WebhookId, tenantId, channelId);
            }
            throw; // re-throw original insert exception
        }

        // 5. Audit: webhook_created — payload contains webhook ID, NOT the token.
        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: userId,
            EventType: AuditEventTypes.ChannelWebhookCreated,
            Payload: new Dictionary<string, object?>
            {
                ["channel_id"] = channelId,
                ["discord_webhook_id"] = (long)created.WebhookId,
            }), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Webhook created for channel {ChannelId} tenant {TenantId} (webhook {WebhookId})",
            channelId, tenantId, created.WebhookId);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task DeleteForChannelAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default)
    {
        // 1. Load the webhook row.
        var row = await _repo.GetByChannelAsync(tenantId, channelId, ct).ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogInformation(
                "No webhook row found for channel {ChannelId} tenant {TenantId} — nothing to delete.",
                channelId, tenantId);
            return; // idempotent — already clean
        }

        // 2. Decrypt token to call Discord DELETE.
        //    If decryption fails (e.g., key rotation issue), skip the Discord call
        //    and still clean up the DB row.
        string? token = null;
        try
        {
            var envelope = new ChannelWebhookEnvelope(
                row.Ciphertext, row.Nonce, row.AuthTag, row.KeyVersion);
            token = _cipher.Decrypt(envelope);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not decrypt webhook token for channel {ChannelId} tenant {TenantId} — " +
                "skipping Discord DELETE, proceeding with DB cleanup.",
                channelId, tenantId);
        }

        // 3. Call Discord DELETE webhook if we have a valid token.
        //    404 → already gone — fine (DeleteWebhookAsync returns false).
        //    403/401 → DiscordPermissionException — log + continue (DB cleanup is primary).
        if (token is not null)
        {
            try
            {
                await _discord.DeleteWebhookAsync(
                    (ulong)row.DiscordWebhookId, token, ct).ConfigureAwait(false);
            }
            catch (DiscordPermissionException ex)
            {
                _logger.LogWarning(ex,
                    "Lost permission to delete Discord webhook {WebhookId} for channel {ChannelId} " +
                    "tenant {TenantId} — continuing with DB cleanup.",
                    row.DiscordWebhookId, channelId, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error deleting Discord webhook {WebhookId} for channel {ChannelId} " +
                    "tenant {TenantId} — continuing with DB cleanup.",
                    row.DiscordWebhookId, channelId, tenantId);
            }
        }

        // 4. Delete from DB.
        await _repo.DeleteByChannelAsync(tenantId, channelId, ct).ConfigureAwait(false);

        // 5. Audit.
        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: null,
            EventType: AuditEventTypes.ChannelWebhookDeleted,
            Payload: new Dictionary<string, object?>
            {
                ["channel_id"] = channelId,
                ["discord_webhook_id"] = row.DiscordWebhookId,
            }), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Webhook deleted for channel {ChannelId} tenant {TenantId} (webhook {WebhookId})",
            channelId, tenantId, row.DiscordWebhookId);
    }

    // ── Decrypt (internal callers only) ──────────────────────────────────────

    /// <inheritdoc />
    public async Task<string> DecryptTokenAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default)
    {
        var row = await _repo.GetByChannelAsync(tenantId, channelId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No webhook found for channel {channelId} (tenant {tenantId}).");
        var envelope = new ChannelWebhookEnvelope(
            row.Ciphertext, row.Nonce, row.AuthTag, row.KeyVersion);
        return _cipher.Decrypt(envelope);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads the <see cref="GuildChannel"/> by its internal PK and decrypts the
    /// guild bot token. The bot token is used only within the caller's scope.
    ///
    /// Because IGuildChannelRepository has no GetByInternalIdAsync, this method
    /// enumerates guilds for the tenant and scans channels per guild until a match
    /// is found. This is O(G × C) where G = guilds per tenant and C = channels per
    /// guild — both bounded in practice.
    /// </summary>
    private async Task<(GuildChannel channel, string botToken)>
        ResolveChannelAndBotTokenAsync(
            long tenantId,
            long channelId,
            CancellationToken ct)
    {
        var guildsWithStatus = await _guilds.ListByTenantWithStatusAsync(tenantId, ct)
            .ConfigureAwait(false);

        GuildChannel? foundChannel = null;
        long foundGuildId = 0;

        foreach (var guildStatus in guildsWithStatus)
        {
            var channelsForGuild = await _channels.ListByGuildAsync(
                tenantId, guildStatus.Guild.Id, ct).ConfigureAwait(false);
            var match = channelsForGuild.FirstOrDefault(c => c.Id == channelId);
            if (match is not null)
            {
                foundChannel = match;
                foundGuildId = guildStatus.Guild.Id;
                break;
            }
        }

        if (foundChannel is null)
            throw new InvalidOperationException(
                $"Channel {channelId} not found for tenant {tenantId}.");

        var credential = await _creds.GetByGuildIdAsync(foundGuildId, tenantId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No bot credentials for guild {foundGuildId} (tenant {tenantId}). " +
                "Configure the bot token before bridging channels.");

        var botToken = _botCipher.Decrypt(new CipherEnvelope(
            credential.Nonce,
            credential.Ciphertext,
            credential.Tag));

        return (foundChannel, botToken);
    }
}
