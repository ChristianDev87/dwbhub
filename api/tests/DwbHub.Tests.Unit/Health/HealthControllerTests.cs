using DwbHub.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DwbHub.Tests.Unit.Health;

public sealed class HealthControllerTests
{
    [Fact]
    public void Get_ReturnsOkWithStatusAndVersion()
    {
        var controller = new HealthController();

        var result = controller.Get() as OkObjectResult;

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(200);
        result.Value.Should().BeEquivalentTo(new
        {
            status = "ok"
        }, options => options.ExcludingMissingMembers());
    }

    [Fact]
    public void Get_PayloadIncludesVersionAndUptime()
    {
        var controller = new HealthController();

        var result = controller.Get() as OkObjectResult;
        var payload = result!.Value!;
        var type = payload.GetType();

        type.GetProperty("version").Should().NotBeNull();
        type.GetProperty("uptime_seconds").Should().NotBeNull();
    }
}
