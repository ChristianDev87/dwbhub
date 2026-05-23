using DwbHub.Core.Entities;
using DwbHub.Infrastructure.Tenancy;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Tenancy;

public sealed class GuildContextTests
{
    [Fact]
    public void Default_state_has_no_current_guild()
    {
        var ctx = new GuildContext();
        ctx.Current.Should().BeNull();
        ctx.IsResolved.Should().BeFalse();
    }

    [Fact]
    public void After_set_returns_assigned_guild()
    {
        var ctx = new GuildContext();
        var guild = new Guild(
            Id: 7, PublicId: Guid.NewGuid(), TenantId: 42,
            DiscordGuildId: "1234567890123456789", DisplayName: "Production",
            IsActive: true, RegisteredByUserId: 99,
            RegisteredAt: DateTimeOffset.UtcNow, LastConnectedAt: null,
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

        ctx.Current = guild;

        ctx.Current.Should().Be(guild);
        ctx.IsResolved.Should().BeTrue();
    }
}
