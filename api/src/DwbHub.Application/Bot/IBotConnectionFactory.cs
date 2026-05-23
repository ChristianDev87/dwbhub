namespace DwbHub.Application.Bot;

/// <summary>
/// Factory abstraction so production (DiscordNetBotConnectionFactory) and tests
/// (FakeBotConnectionFactory) plug in via the same DI seam.
/// </summary>
public interface IBotConnectionFactory
{
    IBotConnection Create(long guildId, long tenantId);
}
