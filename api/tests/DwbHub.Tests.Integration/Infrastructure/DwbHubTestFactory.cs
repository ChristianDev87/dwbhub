using Hangfire.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DwbHub.Tests.Integration.Infrastructure;

/// <summary>
/// Drop-in replacement for <c>WebApplicationFactory&lt;Program&gt;</c> in integration
/// and security tests.
///
/// Removes the Hangfire <see cref="BackgroundProcessingServer"/> from the test host
/// before the host starts. The DI container would otherwise tear down the server
/// object before its background polling loop had a chance to observe the shutdown
/// signal, resulting in a sporadic
/// <see cref="ObjectDisposedException"/> on 'Hangfire.Server.BackgroundProcessingServer'.
///
/// The tests that use this factory only exercise HTTP endpoints and job enqueueing;
/// none of them require actual background-job execution. The Hangfire storage and
/// <see cref="Hangfire.IBackgroundJobClient"/> remain available so that controller
/// code that enqueues jobs still works correctly.
/// </summary>
public sealed class DwbHubTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove every IHostedService registration whose concrete type is (or
            // derives from) Hangfire's BackgroundProcessingServer.  We check both the
            // ImplementationType (most common) and the ImplementationInstance path to
            // be safe.
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

    // Factory-registered services expose the concrete type only via the factory
    // delegate's return type — which is not directly inspectable at registration
    // time.  As a safety net we also filter by the full type name so that even
    // implementation-factory registrations are caught if Hangfire ever switches to
    // that pattern.
    private static bool IsHangfireServerFactory(ServiceDescriptor d) =>
        d.ImplementationFactory is not null &&
        (d.ServiceType.FullName?.Contains("BackgroundProcessingServer") == true ||
         d.ServiceType.Name.Contains("BackgroundProcessingServer"));
}
