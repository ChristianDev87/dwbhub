using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

public sealed class GuildRepository(IDbConnectionFactory connectionFactory) : IGuildRepository
{
    public async Task<(long Id, Guid PublicId)> CreateAsync(
        long tenantId,
        string discordGuildId,
        string displayName,
        long registeredByUserId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO guilds (tenant_id, discord_guild_id, display_name, registered_by_user_id)
            VALUES (@TenantId, @DiscordGuildId, @DisplayName, @RegisteredByUserId)
            RETURNING id, public_id;
            """;
        return await conn.QuerySingleAsync<(long Id, Guid PublicId)>(
            new CommandDefinition(sql,
                new
                {
                    TenantId = tenantId,
                    DiscordGuildId = discordGuildId,
                    DisplayName = displayName,
                    RegisteredByUserId = registeredByUserId,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<Guild?> GetByPublicIdAsync(Guid publicId, long tenantId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, public_id, tenant_id, discord_guild_id, display_name,
                   is_active, registered_by_user_id, registered_at, last_connected_at,
                   created_at, updated_at
            FROM guilds
            WHERE public_id = @PublicId AND tenant_id = @TenantId;
            """;
        return await conn.QuerySingleOrDefaultAsync<Guild>(
            new CommandDefinition(sql,
                new { PublicId = publicId, TenantId = tenantId },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guild>> ListByTenantAsync(long tenantId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, public_id, tenant_id, discord_guild_id, display_name,
                   is_active, registered_by_user_id, registered_at, last_connected_at,
                   created_at, updated_at
            FROM guilds
            WHERE tenant_id = @TenantId
            ORDER BY display_name ASC, id ASC;
            """;
        var rows = await conn.QueryAsync<Guild>(
            new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<bool> DeleteAsync(Guid publicId, long tenantId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            DELETE FROM guilds
            WHERE public_id = @PublicId AND tenant_id = @TenantId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { PublicId = publicId, TenantId = tenantId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }
}
