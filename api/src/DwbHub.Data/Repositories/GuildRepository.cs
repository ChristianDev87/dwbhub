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

    public async Task<(Tenant? Tenant, Guild? Guild)> ResolveTenantAndGuildAsync(
        string slug, Guid guildPublicId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            WITH t AS (
                SELECT id, name, slug, locale, created_at, updated_at
                FROM tenants WHERE slug = @Slug::citext
            ),
            g AS (
                SELECT id, public_id, tenant_id, discord_guild_id, display_name,
                       is_active, registered_by_user_id, registered_at,
                       last_connected_at, created_at, updated_at
                FROM guilds
                WHERE tenant_id = (SELECT id FROM t)
                  AND public_id = @PublicId
            )
            SELECT
                (SELECT id FROM t)         AS tenant_id,
                (SELECT name FROM t)       AS tenant_name,
                (SELECT slug FROM t)       AS tenant_slug,
                (SELECT locale FROM t)     AS tenant_locale,
                (SELECT created_at FROM t) AS tenant_created_at,
                (SELECT updated_at FROM t) AS tenant_updated_at,
                (SELECT id FROM g)                 AS guild_id,
                (SELECT public_id FROM g)          AS guild_public_id,
                (SELECT tenant_id FROM g)          AS guild_tenant_id,
                (SELECT discord_guild_id FROM g)   AS guild_discord_guild_id,
                (SELECT display_name FROM g)       AS guild_display_name,
                (SELECT is_active FROM g)          AS guild_is_active,
                (SELECT registered_by_user_id FROM g) AS guild_registered_by_user_id,
                (SELECT registered_at FROM g)      AS guild_registered_at,
                (SELECT last_connected_at FROM g)  AS guild_last_connected_at,
                (SELECT created_at FROM g)         AS guild_created_at,
                (SELECT updated_at FROM g)         AS guild_updated_at;
            """;

        var row = (IDictionary<string, object?>)await conn.QuerySingleAsync<dynamic>(
            new CommandDefinition(sql,
                new { Slug = slug, PublicId = guildPublicId },
                cancellationToken: ct))
            .ConfigureAwait(false);

        Tenant? tenant = row["tenant_id"] is null ? null : new Tenant(
            Id: (long)row["tenant_id"]!,
            Name: (string)row["tenant_name"]!,
            Slug: (string)row["tenant_slug"]!,
            Locale: (string)row["tenant_locale"]!,
            CreatedAt: (DateTimeOffset)row["tenant_created_at"]!,
            UpdatedAt: (DateTimeOffset)row["tenant_updated_at"]!);

        Guild? guild = row["guild_id"] is null ? null : new Guild(
            Id: (long)row["guild_id"]!,
            PublicId: (Guid)row["guild_public_id"]!,
            TenantId: (long)row["guild_tenant_id"]!,
            DiscordGuildId: (string)row["guild_discord_guild_id"]!,
            DisplayName: (string)row["guild_display_name"]!,
            IsActive: (bool)row["guild_is_active"]!,
            RegisteredByUserId: (long)row["guild_registered_by_user_id"]!,
            RegisteredAt: (DateTimeOffset)row["guild_registered_at"]!,
            LastConnectedAt: (DateTimeOffset?)row["guild_last_connected_at"],
            CreatedAt: (DateTimeOffset)row["guild_created_at"]!,
            UpdatedAt: (DateTimeOffset)row["guild_updated_at"]!);

        return (tenant, guild);
    }
}
