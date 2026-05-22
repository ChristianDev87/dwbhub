using DwbHub.Infrastructure.Email;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Auth;

public sealed class TemplateEmailRendererTests
{
    private readonly TemplateEmailRenderer _sut = new();

    [Fact]
    public void Render_substitutes_all_placeholders_in_VerifyEmail_de()
    {
        var msg = _sut.Render("VerifyEmail", "de", "alice@acme.test", new Dictionary<string, string>
        {
            ["tenantName"] = "Acme",
            ["userDisplayName"] = "Alice",
            ["verifyUrl"] = "https://x.test/t/acme/verify-email?token=abc",
            ["expiresInHours"] = "24",
        });

        msg.ToAddress.Should().Be("alice@acme.test");
        msg.Subject.Should().Contain("Acme").And.Contain("bestätigen");
        msg.HtmlBody.Should().Contain("Alice").And.Contain("Acme").And.Contain("https://x.test/t/acme/verify-email?token=abc").And.Contain("24");
        msg.HtmlBody.Should().NotContain("{{");
        msg.TextBody.Should().Contain("Alice").And.NotContain("<");
    }

    [Fact]
    public void Render_falls_back_to_de_for_unknown_locale()
    {
        var deMsg = _sut.Render("VerifyEmail", "fr", "alice@acme.test", new Dictionary<string, string>
        {
            ["tenantName"] = "Acme",
            ["userDisplayName"] = "Alice",
            ["verifyUrl"] = "https://x",
            ["expiresInHours"] = "24",
        });
        deMsg.Subject.Should().Contain("bestätigen", "unknown locale falls back to de");
    }

    [Fact]
    public void Render_uses_en_when_locale_is_en()
    {
        var msg = _sut.Render("PasswordReset", "en", "alice@acme.test", new Dictionary<string, string>
        {
            ["tenantName"] = "Acme",
            ["userDisplayName"] = "Alice",
            ["resetUrl"] = "https://x",
            ["expiresInMinutes"] = "60",
        });
        msg.Subject.Should().Contain("Reset password");
        msg.HtmlBody.Should().Contain("60");
    }

    [Fact]
    public void Render_throws_on_unknown_template_key()
    {
        var act = () => _sut.Render("NoSuchTemplate", "de", "a@b.test", new Dictionary<string, string>());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not found*");
    }
}
