namespace DwbHub.Core.Entities;

/// <summary>
/// Lightweight projection returned by IGuildRepository.ListActiveWithCredentialsAsync.
/// Holds only the FK pair needed by BotConnectionManager.StartAsync to bootstrap
/// connections — full Guild + Credential data is loaded per-guild on demand.
/// </summary>
public sealed record GuildIdTenantPair(long GuildId, long TenantId);
