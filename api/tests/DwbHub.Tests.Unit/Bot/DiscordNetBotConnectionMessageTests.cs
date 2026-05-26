using DwbHub.Application.Messaging;
using DwbHub.Tests.Integration.Bot;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Bot;

/// <summary>
/// Tests the Application-layer event surface using FakeBotConnection.
/// DiscordNetBotConnection's Discord.NET handler logic is covered by the
/// Discord live tests in Plan 1.0 Task 15 — those need a real Discord
/// gateway, so they can't run as unit tests.
/// </summary>
public sealed class FakeBotConnectionMessageEventsTests
{
    [Fact]
    public async Task RaiseMessageReceived_InvokesSubscribers()
    {
        var fake = new FakeBotConnection(guildId: 1, tenantId: 1);
        var captured = new List<MessageReceivedEvent>();
        fake.MessageReceived += evt =>
        {
            captured.Add(evt);
            return Task.CompletedTask;
        };

        var evt = new MessageReceivedEvent
        {
            TenantId = 1,
            GuildId = 1,
            DiscordChannelId = 100,
            DiscordMessageId = 200,
            DiscordAuthorId = 300,
            DiscordAuthorName = "alice",
            Content = "hi",
            SentAt = DateTimeOffset.UtcNow,
        };
        await fake.RaiseMessageReceivedAsync(evt);

        captured.Should().ContainSingle().Which.Should().Be(evt);
    }

    [Fact]
    public async Task RaiseMessageReceived_WithNoSubscribers_Succeeds()
    {
        var fake = new FakeBotConnection(guildId: 1, tenantId: 1);
        var act = () => fake.RaiseMessageReceivedAsync(new MessageReceivedEvent
        {
            TenantId = 1,
            GuildId = 1,
            DiscordChannelId = 100,
            DiscordMessageId = 200,
            DiscordAuthorId = 300,
            DiscordAuthorName = "alice",
            Content = "hi",
            SentAt = DateTimeOffset.UtcNow,
        });
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RaiseMessageUpdated_InvokesSubscribers()
    {
        var fake = new FakeBotConnection(guildId: 1, tenantId: 1);
        var captured = new List<MessageUpdatedEvent>();
        fake.MessageUpdated += evt => { captured.Add(evt); return Task.CompletedTask; };

        var evt = new MessageUpdatedEvent
        {
            TenantId = 1,
            GuildId = 1,
            DiscordChannelId = 100,
            DiscordMessageId = 200,
            Content = "edited",
            EditedAt = DateTimeOffset.UtcNow,
        };
        await fake.RaiseMessageUpdatedAsync(evt);
        captured.Should().ContainSingle().Which.Should().Be(evt);
    }

    [Fact]
    public async Task RaiseMessageDeleted_InvokesSubscribers()
    {
        var fake = new FakeBotConnection(guildId: 1, tenantId: 1);
        var captured = new List<MessageDeletedEvent>();
        fake.MessageDeleted += evt => { captured.Add(evt); return Task.CompletedTask; };

        var evt = new MessageDeletedEvent
        {
            TenantId = 1,
            GuildId = 1,
            DiscordChannelId = 100,
            DiscordMessageId = 200,
        };
        await fake.RaiseMessageDeletedAsync(evt);
        captured.Should().ContainSingle().Which.Should().Be(evt);
    }
}
