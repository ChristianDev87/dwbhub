using System.Net;
using DwbHub.Infrastructure.Bot;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Bot;

/// <summary>
/// Tests the credential-rejection classifier in <see cref="DiscordNetBotConnection.IsAuthFailure"/>.
/// Both the gateway close-code 4004 path and the REST 401 path must map to
/// "stop the client" so Discord.NET's internal reconnect loop does not keep
/// retrying with a bad token until the API rate-limits or bans us.
/// </summary>
public sealed class DiscordNetBotConnectionAuthFailureTests
{
    [Fact]
    public void IsAuthFailure_null_returns_false()
    {
        DiscordNetBotConnection.IsAuthFailure(null).Should().BeFalse();
    }

    [Fact]
    public void IsAuthFailure_generic_exception_returns_false()
    {
        DiscordNetBotConnection.IsAuthFailure(new InvalidOperationException("nope"))
            .Should().BeFalse();
    }

    [Fact]
    public void IsAuthFailure_websocket_closed_with_code_4004_returns_true()
    {
        var ex = new Discord.Net.WebSocketClosedException(4004, "AUTHENTICATION_FAILED");
        DiscordNetBotConnection.IsAuthFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void IsAuthFailure_websocket_closed_with_code_4000_returns_false()
    {
        // 4000 = generic gateway error, not a credential rejection.
        var ex = new Discord.Net.WebSocketClosedException(4000, "unknown error");
        DiscordNetBotConnection.IsAuthFailure(ex).Should().BeFalse();
    }

    [Fact]
    public void IsAuthFailure_websocket_closed_with_code_1006_returns_false()
    {
        // 1006 = abnormal closure (network blip).
        var ex = new Discord.Net.WebSocketClosedException(1006, "connection reset");
        DiscordNetBotConnection.IsAuthFailure(ex).Should().BeFalse();
    }

    [Fact]
    public void IsAuthFailure_http_401_returns_true()
    {
        var ex = new Discord.Net.HttpException(HttpStatusCode.Unauthorized, null, null);
        DiscordNetBotConnection.IsAuthFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void IsAuthFailure_http_403_returns_false()
    {
        // 403 = missing permission, not bad credentials.
        var ex = new Discord.Net.HttpException(HttpStatusCode.Forbidden, null, null);
        DiscordNetBotConnection.IsAuthFailure(ex).Should().BeFalse();
    }

    [Fact]
    public void IsAuthFailure_http_500_returns_false()
    {
        var ex = new Discord.Net.HttpException(HttpStatusCode.InternalServerError, null, null);
        DiscordNetBotConnection.IsAuthFailure(ex).Should().BeFalse();
    }
}
