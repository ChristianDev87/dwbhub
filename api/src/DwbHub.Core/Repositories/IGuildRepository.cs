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
    /// PostgresException with SqlState 23505. Returns the new internal id + public_id.
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
}
