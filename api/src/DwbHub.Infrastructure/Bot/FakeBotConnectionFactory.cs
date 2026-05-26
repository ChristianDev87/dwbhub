using DwbHub.Application.Bot;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Bot;

/// <summary>
/// Factory that hands out <see cref="FakeBotConnection"/> instances when the API
/// runs in test mode (<c>DWBHUB_DISCORD_TEST_MODE=fake-rest</c>). Registered as
/// the <see cref="IBotConnectionFactory"/> in place of the Discord.NET-backed
/// factory so the bot manager never opens a real gateway connection.
/// </summary>
public sealed class FakeBotConnectionFactory : IBotConnectionFactory
{
    private readonly IHostEnvironment _env;
    private readonly ILoggerFactory _loggerFactory;

    public FakeBotConnectionFactory(IHostEnvironment env, ILoggerFactory loggerFactory)
    {
        if (env.IsProduction())
            throw new InvalidOperationException(
                "FakeBotConnectionFactory must NEVER be activated in Production.");
        _env = env;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public IBotConnection Create(long guildId, long tenantId)
        => new FakeBotConnection(guildId, tenantId, _env, _loggerFactory.CreateLogger<FakeBotConnection>());
}
