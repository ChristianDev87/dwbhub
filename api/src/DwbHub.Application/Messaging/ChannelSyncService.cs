using DwbHub.Application.Audit;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace DwbHub.Application.Messaging;

/// <summary>
/// Fetches all Discord channels for a guild via REST and upserts them into
/// guild_channels. Never touches is_bridged or bridged_at — those are owner-managed.
/// </summary>
public sealed class ChannelSyncService : IChannelSyncService
{
    private readonly IGuildRepository _guilds;
    private readonly IGuildBotCredentialRepository _creds;
    private readonly IBotTokenEncryptor _botCipher;
    private readonly IGuildChannelRepository _channels;
    private readonly IDiscordRestChannelClient _discord;
    private readonly IAuditWriter _audit;
    private readonly ILogger<ChannelSyncService> _logger;

    public ChannelSyncService(
        IGuildRepository guilds,
        IGuildBotCredentialRepository creds,
        IBotTokenEncryptor botCipher,
        IGuildChannelRepository channels,
        IDiscordRestChannelClient discord,
        IAuditWriter audit,
        ILogger<ChannelSyncService> logger)
    {
        _guilds = guilds;
        _creds = creds;
        _botCipher = botCipher;
        _channels = channels;
        _discord = discord;
        _audit = audit;
        _logger = logger;
    }

    public async Task SyncFromDiscordAsync(
        long tenantId,
        Guid guildPublicId,
        CancellationToken ct = default)
    {
        var guild = await _guilds.GetByPublicIdAsync(guildPublicId, tenantId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Guild {guildPublicId} not found for tenant {tenantId}.");

        var credential = await _creds.GetByGuildIdAsync(guild.Id, tenantId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No bot credentials for guild {guild.Id} (tenant {tenantId}). Configure the bot token first.");

        var botToken = _botCipher.Decrypt(new CipherEnvelope(
            credential.Nonce,
            credential.Ciphertext,
            credential.Tag));

        var discordChannels = await _discord.ListChannelsAsync(
            botToken,
            discordGuildId: ulong.Parse(guild.DiscordGuildId),
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Syncing {Count} channels for guild {GuildId} tenant {TenantId}",
            discordChannels.Count, guild.Id, tenantId);

        int added = 0, updated = 0;

        foreach (var dc in discordChannels)
        {
            var existing = await _channels.GetByDiscordIdAsync(
                tenantId, (long)dc.Id, ct).ConfigureAwait(false);

            await _channels.UpsertFromSyncAsync(
                tenantId: tenantId,
                guildId: guild.Id,
                discordChannelId: (long)dc.Id,
                name: dc.Name,
                channelType: dc.Type,
                position: dc.Position,
                ct: ct).ConfigureAwait(false);

            if (existing is null) added++;
            else updated++;
        }

        await _audit.RecordAsync(new AuditEvent(
            TenantId: tenantId,
            ActorUserId: null,
            EventType: AuditEventTypes.ChannelSyncCompleted,
            Payload: new Dictionary<string, object?>
            {
                ["guild_id"] = guild.Id,
                ["added_count"] = added,
                ["updated_count"] = updated,
                ["total_count"] = discordChannels.Count,
            }), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Channel sync complete for guild {GuildId}: added={Added} updated={Updated}",
            guild.Id, added, updated);
    }
}
