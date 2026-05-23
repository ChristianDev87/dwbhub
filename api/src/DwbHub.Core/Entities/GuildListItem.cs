namespace DwbHub.Core.Entities;

/// <summary>
/// View-only wrapper around <see cref="Guild"/> that additionally reports whether
/// the guild has bot credentials configured. Produced by
/// <c>IGuildRepository.ListByTenantWithStatusAsync</c> via a single LEFT JOIN —
/// 0 additional round-trips vs. <see cref="Guild"/>-only listing.
/// </summary>
public sealed record GuildListItem(Guild Guild, bool BotCredentialsConfigured);
