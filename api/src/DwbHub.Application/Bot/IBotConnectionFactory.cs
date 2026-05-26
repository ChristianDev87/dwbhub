namespace DwbHub.Application.Bot;

/// <summary>
/// Factory abstraction so production (DiscordNetBotConnectionFactory) and tests
/// (FakeBotConnectionFactory) plug in via the same DI seam.
/// </summary>
public interface IBotConnectionFactory
{
    /// <summary>Create and return a new <see cref="IBotConnection"/> for the given guild and tenant.</summary>
    /// <param name="guildId">Internal database ID of the guild whose bot connection to create.</param>
    /// <param name="tenantId">Internal database ID of the owning tenant.</param>
    /// <returns>A new, unstarted <see cref="IBotConnection"/> instance.</returns>
    IBotConnection Create(long guildId, long tenantId);
}
