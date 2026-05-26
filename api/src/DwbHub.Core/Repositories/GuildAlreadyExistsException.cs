namespace DwbHub.Core.Repositories;

/// <summary>
/// Thrown by <see cref="IGuildRepository.CreateAsync"/> when a guild with the
/// same (tenant_id, discord_guild_id) already exists. Replaces the previous
/// pattern of letting Npgsql's <c>PostgresException</c> (SqlState 23505) bubble
/// up: the implementation now uses <c>INSERT ... ON CONFLICT DO NOTHING</c>
/// at the SQL level so PostgreSQL does NOT log an ERROR for the violation,
/// then raises this application-level exception when zero rows were inserted.
///
/// Plan 1.0 / Fix C lesson: the previous "let the constraint throw" pattern
/// flooded postgres logs with one ERROR per test-seed re-run (~25 per e2e
/// suite), drowning out real bugs. The Task 14.5 server-side error scanner
/// would flag the noise on every PR — counter-productive. Catching at the
/// SQL level keeps the logs clean while preserving the controller's 409 path.
/// </summary>
public sealed class GuildAlreadyExistsException(long tenantId, string discordGuildId)
    : Exception($"Guild already exists for tenant {tenantId} + discord_guild_id {discordGuildId}")
{
    public long TenantId { get; } = tenantId;
    public string DiscordGuildId { get; } = discordGuildId;
}
