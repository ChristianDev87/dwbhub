using DwbHub.Application.Auth;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Auth;

public sealed class PasswordStrengthTests
{
    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData(null)]
    public void Validate_returns_TooShort_for_lt_8_chars(string? input)
    {
        PasswordStrength.Validate(input).Should().Be(PasswordStrengthResult.TooShort);
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("correct horse battery staple")]
    public void Validate_returns_Strong_for_gte_8_chars(string input)
    {
        PasswordStrength.Validate(input).Should().Be(PasswordStrengthResult.Strong);
    }
}
