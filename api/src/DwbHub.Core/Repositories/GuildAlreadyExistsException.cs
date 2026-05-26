namespace DwbHub.Core.Repositories;

/// <summary>
/// Thrown when an attempt to create a guild collides with an existing
/// (tenant_id, discord_guild_id) row.
/// </summary>
/// <remarks>
/// The repository uses <c>INSERT … ON CONFLICT DO NOTHING</c> so PostgreSQL
/// does not log a 23505 error for the violation, then raises this exception
/// when zero rows were inserted. This keeps the postgres log clean for the
/// duplicate-attempt case, which happens legitimately during test seeding.
/// </remarks>
public sealed class GuildAlreadyExistsException(long tenantId, string discordGuildId)
    : Exception($"Guild already exists for tenant {tenantId} + discord_guild_id {discordGuildId}")
{
    public long TenantId { get; } = tenantId;
    public string DiscordGuildId { get; } = discordGuildId;
}
