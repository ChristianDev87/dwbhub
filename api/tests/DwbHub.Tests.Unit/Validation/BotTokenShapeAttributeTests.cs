using System.ComponentModel.DataAnnotations;
using DwbHub.Application.Validation;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Validation;

public sealed class BotTokenShapeAttributeTests
{
    private static bool IsValid(string? value)
    {
        var attr = new BotTokenShapeAttribute();
        var ctx = new ValidationContext(new object());
        return attr.GetValidationResult(value, ctx) == ValidationResult.Success;
    }

    [Theory]
    [InlineData("TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.BBBBBB.CCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")]
    public void Valid_shapes_pass(string token) => IsValid(token).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    [InlineData("abcdef.ghijkl.mnopqr.stuvwx")]
    [InlineData(".....................................................")]
    public void Invalid_shapes_fail(string token)
    {
        // Empty/null gets ValidationResult.Success from the attribute (it leaves null-check
        // to [Required]). For strings, expect failure.
        if (string.IsNullOrEmpty(token))
        {
            IsValid(token).Should().BeTrue(
                "the attribute defers null/empty to [Required] — combined validation handles those");
            return;
        }
        IsValid(token).Should().BeFalse();
    }
}
