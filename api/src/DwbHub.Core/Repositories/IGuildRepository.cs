using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Guild-table operations. Every method takes a `long tenantId` to enforce
/// tenant scoping at the data layer (defense-in-depth alongside middleware).
/// </summary>
public interface IGuildRepository
{
    /// <summary>
    /// Inserts a new guild. Collision on (tenant_id, discord_guild_id) raises
    /// <see cref="GuildAlreadyExistsException"/>. Returns the new internal id + public_id.
    ///
    /// Uses <c>INSERT … ON CONFLICT DO NOTHING</c> to avoid PostgreSQL ERROR-level log noise on expected duplicate attempts.
    /// </summary>
    Task<(long Id, Guid PublicId)> CreateAsync(
        long tenantId,
        string discordGuildId,
        string displayName,
        long registeredByUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads a guild by its external UUID, scoped to a tenant. Returns null
    /// when the UUID does not exist OR belongs to a different tenant
    /// (caller cannot distinguish — info-leak protection).
    /// </summary>
    Task<Guild?> GetByPublicIdAsync(Guid publicId, long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Lists all guilds for a tenant, ordered by display_name. No paging in Phase 0.
    /// </summary>
    Task<IReadOnlyList<Guild>> ListByTenantAsync(long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a guild by public_id, tenant-scoped. Returns true iff a row was deleted.
    /// </summary>
    Task<bool> DeleteAsync(Guid publicId, long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Resolves tenant + guild in a single Postgres round-trip via a combined CTE.
    /// Returns:
    ///   - (null, null) when the slug does not match any tenant.
    ///   - (tenant, null) when the slug matches but the publicId does not exist
    ///     in this tenant (or exists only in a different tenant — info-leak protection).
    ///   - (tenant, guild) on full resolution.
    /// </summary>
    Task<(DwbHub.Core.Entities.Tenant? Tenant, DwbHub.Core.Entities.Guild? Guild)>
        ResolveTenantAndGuildAsync(string slug, Guid guildPublicId, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="ListByTenantAsync"/> but additionally reports whether
    /// each guild has bot credentials configured. Single query via LEFT JOIN — 0
    /// additional round-trips vs. <see cref="ListByTenantAsync"/>. Used by the
    /// frontend GuildsPage to render status badges.
    /// </summary>
    Task<IReadOnlyList<GuildListItem>> ListByTenantWithStatusAsync(
        long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Loads a guild by its internal id. Returns null when the id does not exist.
    /// Used by BotConnectionManager to re-validate guild state before connecting.
    /// </summary>
    Task<Guild?> GetByIdAsync(long guildId, CancellationToken ct = default);

    /// <summary>
    /// Returns the (guild_id, tenant_id) pairs of every guild that is active AND has
    /// bot credentials configured. Used at API startup to bootstrap all connections.
    /// Single round-trip via INNER JOIN guilds + guild_bot_credentials.
    /// </summary>
    Task<IReadOnlyList<GuildIdTenantPair>> ListActiveWithCredentialsAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the is_active flag for a guild. Returns true iff the row's value actually changed
    /// (i.e. setting active=true on a guild that was already active returns false).
    /// </summary>
    Task<bool> SetActiveAsync(long guildId, long tenantId, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// Updates the last_connected_at timestamp. Used by BotConnectionManager on every
    /// successful state-transition to Connected. Fire-and-forget OK.
    /// </summary>
    Task UpdateLastConnectedAtAsync(long guildId, DateTimeOffset timestamp, CancellationToken ct = default);

    /// <summary>
    /// Persist the result of a Discord GET /guilds/{id}/members/@me permission check.
    /// Tenant-scoped to prevent cross-tenant updates.
    /// </summary>
    Task UpdateBotPermissionsAsync(long guildId, long tenantId, bool canManageMessages, CancellationToken ct = default);
}
