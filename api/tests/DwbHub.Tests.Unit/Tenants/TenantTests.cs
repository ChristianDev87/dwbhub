using DwbHub.Core.Entities;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Tenants;

public sealed class TenantTests
{
    [Fact]
    public void Tenant_record_uses_value_equality()
    {
        var ts = new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero);
        var a = new Tenant(1, "Acme", "acme", "de", ts, ts);
        var b = new Tenant(1, "Acme", "acme", "de", ts, ts);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Tenant_record_distinguishes_when_any_field_differs()
    {
        var ts = new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero);
        var a = new Tenant(1, "Acme", "acme", "de", ts, ts);
        var b = a with { Id = 2 };

        a.Should().NotBe(b);
    }
}
