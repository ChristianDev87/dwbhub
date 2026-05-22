namespace DwbHub.Application.Auth;

/// <summary>
/// Minimum-length password validator. Length-only follows NIST SP 800-63B 2025
/// guidance: length beats forced complexity. Future plans can add a zxcvbn
/// common-password check.
/// </summary>
public static class PasswordStrength
{
    public const int MinimumLength = 8;

    public static PasswordStrengthResult Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
        {
            return PasswordStrengthResult.TooShort;
        }
        return PasswordStrengthResult.Strong;
    }
}

public enum PasswordStrengthResult
{
    Strong,
    TooShort,
}
