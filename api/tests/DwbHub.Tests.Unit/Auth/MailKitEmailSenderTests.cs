using DwbHub.Infrastructure.Email;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Auth;

public sealed class MailKitEmailSenderTests
{
    [Fact]
    public void Constructor_throws_when_host_is_empty()
    {
        var act = () => new MailKitEmailSender(host: "", port: 1025, from: "DwbHub <noreply@x.test>");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DWBHUB_SMTP_HOST*");
    }

    [Fact]
    public void Constructor_throws_when_port_is_zero()
    {
        var act = () => new MailKitEmailSender(host: "mailpit", port: 0, from: "DwbHub <noreply@x.test>");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DWBHUB_SMTP_PORT*");
    }

    [Fact]
    public void Constructor_throws_when_from_is_empty()
    {
        var act = () => new MailKitEmailSender(host: "mailpit", port: 1025, from: "");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DWBHUB_SMTP_FROM*");
    }

    [Fact]
    public void Constructor_accepts_valid_parameters()
    {
        var sut = new MailKitEmailSender(host: "mailpit", port: 1025, from: "DwbHub <noreply@x.test>");
        sut.Should().NotBeNull();
    }
}
