using Discord;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Bot;

/// <summary>
/// Verifies that Discord.NET's <see cref="GuildPermissions.ManageMessages"/> property
/// correctly extracts bit 13 from a raw permission bitmask.
///
/// <see cref="DwbHub.Infrastructure.Bot.DiscordNetBotConnection"/> uses
/// <c>guild.CurrentUser.GuildPermissions.ManageMessages</c> directly (no wrapper helper).
/// These tests document the bit-13 contract so future maintainers can verify the
/// Discord.NET library behaviour hasn't changed under them.
/// </summary>
public sealed class DiscordPermissionParsingTests
{
    [Fact]
    public void GuildPermissions_ManageMessages_extracts_bit_13()
    {
        var perms = new GuildPermissions(rawValue: 1UL << 13);
        perms.ManageMessages.Should().BeTrue();
    }

    [Fact]
    public void GuildPermissions_ManageMessages_false_when_bit_clear()
    {
        var perms = new GuildPermissions(rawValue: 0UL);
        perms.ManageMessages.Should().BeFalse();
    }

    [Fact]
    public void GuildPermissions_ManageMessages_true_in_combined_bitmask()
    {
        var perms = new GuildPermissions(rawValue: (1UL << 13) | (1UL << 16) | (1UL << 11));
        perms.ManageMessages.Should().BeTrue();
    }
}
