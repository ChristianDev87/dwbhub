using System.Security.Claims;
using DwbHub.Infrastructure.Background;
using FluentAssertions;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DwbHub.Tests.Unit.Background;

public sealed class OwnerOnlyHangfireAuthFilterTests
{
    private static DashboardContext MakeContext(ClaimsPrincipal user)
    {
        // AspNetCoreDashboardContext calls httpContext.RequestServices.GetService<T>()
        // during construction, so we must provide a minimal IServiceProvider.
        var services = new ServiceCollection().BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            User = user,
            RequestServices = services,
        };
        // Hangfire.AspNetCore provides AspNetCoreDashboardContext that wraps an HttpContext.
        // The type lives in Hangfire.Dashboard namespace (not Hangfire.AspNetCore).
        var storage = new Hangfire.MemoryStorage.MemoryStorage();
        return new Hangfire.Dashboard.AspNetCoreDashboardContext(
            storage: storage,
            options: new Hangfire.DashboardOptions(),
            httpContext: http);
    }

    [Fact]
    public void Unauthenticated_user_is_denied()
    {
        var anon = new ClaimsPrincipal(new ClaimsIdentity()); // no AuthenticationType -> not authenticated
        var ctx = MakeContext(anon);
        new OwnerOnlyHangfireAuthFilter().Authorize(ctx).Should().BeFalse();
    }

    [Fact]
    public void Authenticated_non_owner_is_denied()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim("role", "Member"),
        }, authenticationType: "Bearer");
        var ctx = MakeContext(new ClaimsPrincipal(identity));
        new OwnerOnlyHangfireAuthFilter().Authorize(ctx).Should().BeFalse();
    }

    [Fact]
    public void Authenticated_owner_is_allowed()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim("role", "Owner"),
        }, authenticationType: "Bearer");
        var ctx = MakeContext(new ClaimsPrincipal(identity));
        new OwnerOnlyHangfireAuthFilter().Authorize(ctx).Should().BeTrue();
    }
}
