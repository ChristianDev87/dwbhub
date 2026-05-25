using DwbHub.Core.Messaging;
using FluentAssertions;

namespace DwbHub.Tests.Unit.Messaging;

public sealed class MessageTests
{
    [Fact]
    public void Message_Defaults_AreSafe()
    {
        var msg = new Message
        {
            Id = 1,
            TenantId = 1,
            ChannelId = 1,
            DiscordMessageId = 12345,
            DiscordAuthorId = 67890,
            DiscordAuthorName = "alice",
            Content = "hello",
            SentAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        msg.ViaDwbhub.Should().BeFalse();
        msg.DwbhubUserId.Should().BeNull();
        msg.EditedAt.Should().BeNull();
        msg.DeletedAt.Should().BeNull();
    }

    [Fact]
    public void BackfillStatus_AllValues_RoundtripStringConversion()
    {
        // Defensive: Plan 1.0 repos convert enum <-> lowercase string for DB.
        // Make sure every enum value has a stable lowercase name.
        foreach (BackfillStatus value in Enum.GetValues<BackfillStatus>())
        {
            var s = value.ToString().ToLowerInvariant();
            Enum.TryParse<BackfillStatus>(s, ignoreCase: true, out var parsed).Should().BeTrue();
            parsed.Should().Be(value);
        }
    }
}
