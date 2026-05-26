using Dapper;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

public sealed class GuildChannelRepository(IDbConnectionFactory connectionFactory) : IGuildChannelRepository
{
    public async Task<GuildChannel?> GetByPublicIdAsync(
        long tenantId,
        Guid publicId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, guild_id, public_id, discord_channel_id, name,
                   channel_type, position, is_bridged, bridged_at, last_synced_at,
                   created_at, updated_at
            FROM guild_channels
            WHERE tenant_id = @TenantId
              AND public_id = @PublicId;
            """;
        return await conn.QuerySingleOrDefaultAsync<GuildChannel>(
            new CommandDefinition(sql,
                new { TenantId = tenantId, PublicId = publicId },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<GuildChannel?> GetByDiscordIdAsync(
        long tenantId,
        long discordChannelId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, guild_id, public_id, discord_channel_id, name,
                   channel_type, position, is_bridged, bridged_at, last_synced_at,
                   created_at, updated_at
            FROM guild_channels
            WHERE tenant_id          = @TenantId
              AND discord_channel_id = @DiscordChannelId;
            """;
        return await conn.QuerySingleOrDefaultAsync<GuildChannel>(
            new CommandDefinition(sql,
                new { TenantId = tenantId, DiscordChannelId = discordChannelId },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GuildChannel>> ListByGuildAsync(
        long tenantId,
        long guildId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, guild_id, public_id, discord_channel_id, name,
                   channel_type, position, is_bridged, bridged_at, last_synced_at,
                   created_at, updated_at
            FROM guild_channels
            WHERE tenant_id = @TenantId
              AND guild_id  = @GuildId
            ORDER BY position ASC, id ASC;
            """;
        var rows = await conn.QueryAsync<GuildChannel>(
            new CommandDefinition(sql,
                new { TenantId = tenantId, GuildId = guildId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GuildChannel>> ListBridgedAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, guild_id, public_id, discord_channel_id, name,
                   channel_type, position, is_bridged, bridged_at, last_synced_at,
                   created_at, updated_at
            FROM guild_channels
            WHERE tenant_id = @TenantId
              AND is_bridged = true
            ORDER BY id ASC;
            """;
        var rows = await conn.QueryAsync<GuildChannel>(
            new CommandDefinition(sql,
                new { TenantId = tenantId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<GuildChannel> UpsertFromSyncAsync(
        long tenantId,
        long guildId,
        long discordChannelId,
        string name,
        short channelType,
        int position,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            WITH upsert AS (
                INSERT INTO guild_channels (tenant_id, guild_id, discord_channel_id, name, channel_type, position)
                VALUES (@TenantId, @GuildId, @DiscordChannelId, @Name, @ChannelType, @Position)
                ON CONFLICT (tenant_id, discord_channel_id) DO UPDATE
                    SET name           = EXCLUDED.name,
                        channel_type   = EXCLUDED.channel_type,
                        position       = EXCLUDED.position,
                        last_synced_at = now(),
                        updated_at     = now()
                RETURNING *
            )
            SELECT * FROM upsert;
            """;
        return await conn.QuerySingleAsync<GuildChannel>(
            new CommandDefinition(sql,
                new
                {
                    TenantId = tenantId,
                    GuildId = guildId,
                    DiscordChannelId = discordChannelId,
                    Name = name,
                    ChannelType = channelType,
                    Position = position,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<bool> SetBridgedAsync(
        long tenantId,
        Guid publicId,
        bool isBridged,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);

        // When setting is_bridged = true: preserve first-bridge timestamp by using COALESCE.
        // When setting is_bridged = false: leave bridged_at intact for audit.
        const string sql = """
            UPDATE guild_channels
               SET is_bridged  = @IsBridged,
                   bridged_at  = CASE
                                     WHEN @IsBridged THEN COALESCE(bridged_at, now())
                                     ELSE bridged_at
                                 END,
                   updated_at  = now()
             WHERE tenant_id = @TenantId
               AND public_id = @PublicId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, PublicId = publicId, IsBridged = isBridged },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }
}
