using DwbHub.Core.Entities;
using DwbHub.Infrastructure.Tenancy;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Tenancy;

public sealed class TenantContextTests
{
    [Fact]
    public void Default_state_has_no_current_tenant()
    {
        var ctx = new TenantContext();
        ctx.Current.Should().BeNull();
        ctx.IsResolved.Should().BeFalse();
    }

    [Fact]
    public void After_set_returns_assigned_tenant()
    {
        var ctx = new TenantContext();
        var tenant = new Tenant(
            Id: 42, Name: "Acme", Slug: "acme", Locale: "de",
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

        ctx.Current = tenant;

        ctx.Current.Should().Be(tenant);
        ctx.IsResolved.Should().BeTrue();
    }
}
