using Hangfire.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DwbHub.Tests.Integration.RoundTrip;

/// <summary>
/// WebApplicationFactory for Discord round-trip tests.
///
/// Compared to <see cref="Infrastructure.DwbHubTestFactory"/>, this factory makes ONE
/// important difference: it does NOT replace any Discord-facing services.
/// Specifically:
///   - IBotConnectionFactory  → DiscordNetBotConnectionFactory  (real gateway connection)
///   - IDiscordRestChannelClient → DiscordRestChannelClient      (real Discord REST calls)
///   - IBotTokenEncryptor     → AesGcmBotTokenEncryptor          (real AES-GCM encryption)
///
/// The only thing it removes is the Hangfire BackgroundProcessingServer, which is
/// removed for the same reason as in DwbHubTestFactory: the server tears down
/// concurrently with the test host disposal, causing sporadic ObjectDisposedException.
/// Background jobs are not exercised by round-trip tests.
///
/// IMPORTANT: Tests using this factory MUST NOT set DWBHUB_DISCORD_TEST_MODE to
/// "fake-rest". The default (unset or any other value) activates the production code
/// path through the DI switch in Program.cs.
/// </summary>
public sealed class DwbHubRoundTripTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove Hangfire BackgroundProcessingServer — same fix as DwbHubTestFactory.
            // See DwbHubTestFactory.cs for the full reasoning.
            var toRemove = services
                .Where(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    (IsHangfireServer(d.ImplementationType) ||
                     IsHangfireServer(d.ImplementationInstance?.GetType()) ||
                     IsHangfireServerFactory(d)))
                .ToList();

            foreach (var d in toRemove)
                services.Remove(d);
        });
    }

    private static bool IsHangfireServer(Type? t) =>
        t is not null && typeof(BackgroundProcessingServer).IsAssignableFrom(t);

    private static bool IsHangfireServerFactory(ServiceDescriptor d) =>
        d.ImplementationFactory is not null &&
        (d.ServiceType.FullName?.Contains("BackgroundProcessingServer") == true ||
         d.ServiceType.Name.Contains("BackgroundProcessingServer"));
}
