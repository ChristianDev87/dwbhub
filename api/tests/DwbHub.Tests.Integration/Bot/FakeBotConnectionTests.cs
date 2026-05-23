using DwbHub.Application.Bot;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Integration.Bot;

public sealed class FakeBotConnectionTests
{
    [Fact]
    public async Task Connect_with_default_outcome_transitions_Disconnected_to_Connecting_to_Connected()
    {
        var fake = new FakeBotConnection(guildId: 1, tenantId: 1);
        var transitions = new List<BotConnectionStateChange>();
        fake.StateChanged += change => { transitions.Add(change); return Task.CompletedTask; };

        await fake.ConnectAsync("token-xyz", CancellationToken.None);

        fake.State.Should().Be(BotConnectionState.Connected);
        fake.LastConnectedAt.Should().NotBeNull();
        fake.ConnectCallsWithTokens.Should().ContainSingle().Which.Should().Be("token-xyz");
        transitions.Select(t => t.To).Should().ContainInOrder(
            BotConnectionState.Connecting, BotConnectionState.Connected);
    }

    [Fact]
    public async Task Connect_with_TokenInvalid_outcome_does_not_set_LastConnectedAt()
    {
        var fake = new FakeBotConnection(guildId: 2, tenantId: 1)
        {
            ConnectOutcome = BotConnectionState.TokenInvalid,
            ConnectOutcomeError = "gateway_unauthorized",
        };

        await fake.ConnectAsync("bad-token", CancellationToken.None);

        fake.State.Should().Be(BotConnectionState.TokenInvalid);
        fake.LastConnectedAt.Should().BeNull();
    }

    [Fact]
    public async Task Disconnect_after_Connected_transitions_to_Disconnected_idempotent()
    {
        var fake = new FakeBotConnection(guildId: 3, tenantId: 1);
        await fake.ConnectAsync("t", CancellationToken.None);
        await fake.DisconnectAsync(CancellationToken.None);
        await fake.DisconnectAsync(CancellationToken.None);  // idempotent

        fake.State.Should().Be(BotConnectionState.Disconnected);
        fake.DisconnectCalls.Should().Be(2);  // both calls recorded
    }
}
