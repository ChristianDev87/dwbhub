using DwbHub.Application.Bot;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Bot;

public sealed class DiscordNetBotConnectionFactory(ILoggerFactory loggerFactory) : IBotConnectionFactory
{
    public IBotConnection Create(long guildId, long tenantId)
        => new DiscordNetBotConnection(guildId, tenantId, loggerFactory.CreateLogger<DiscordNetBotConnection>());
}
