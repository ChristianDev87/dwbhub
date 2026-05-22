using DwbHub.Application.Setup;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Setup;

public sealed class SlugValidatorTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("acme-corp")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("acme-corp-2025")]
    [InlineData("12345678901234567890123456789012")] // exactly 32 chars
    public void IsValid_returns_true_for_valid_slugs(string input)
    {
        SlugValidator.IsValid(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-acme")]                                // leading hyphen
    [InlineData("acme-")]                                // trailing hyphen
    [InlineData("Acme")]                                 // uppercase
    [InlineData("ac me")]                                // space
    [InlineData("ac_me")]                                // underscore
    [InlineData("123456789012345678901234567890123")]    // 33 chars
    public void IsValid_returns_false_for_invalid_slugs(string? input)
    {
        SlugValidator.IsValid(input).Should().BeFalse();
    }
}
