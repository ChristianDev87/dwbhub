using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace DwbHub.Application.Validation;

/// <summary>
/// Loose structural check for Discord bot tokens. Requires exactly two dots
/// (the three-segment format that has been stable since 2015) and at least 30
/// alphanumeric characters total. Combined with a <c>[RegularExpression]</c>
/// attribute on the DTO property (<c>^[A-Za-z0-9._-]{50,200}$</c>), this rejects
/// obvious garbage without false-rejecting legitimate token-format changes
/// that stay within the three-segment shape.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class BotTokenShapeAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string s || string.IsNullOrEmpty(s))
        {
            // [Required] on the property handles null/empty; the attribute itself
            // returns Success for missing values so it can be combined freely.
            return ValidationResult.Success;
        }

        var dotCount = s.Count(c => c == '.');
        var alphanumCount = s.Count(char.IsLetterOrDigit);

        return (dotCount == 2 && alphanumCount >= 30)
            ? ValidationResult.Success
            : new ValidationResult("invalid_bot_token_format");
    }
}
