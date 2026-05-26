using DwbHub.Application.Bot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Bot;

/// <summary>
/// <see cref="IBotConnectionFactory"/> that creates <see cref="DiscordNetBotConnection"/> instances,
/// each with its own <see cref="Discord.WebSocket.DiscordSocketClient"/>.
/// </summary>
public sealed class DiscordNetBotConnectionFactory(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory) : IBotConnectionFactory
{
    /// <inheritdoc/>
    public IBotConnection Create(long guildId, long tenantId)
        => new DiscordNetBotConnection(
            guildId, tenantId,
            loggerFactory.CreateLogger<DiscordNetBotConnection>(),
            scopeFactory);
}
