namespace DwbHub.Application.Auth;

/// <summary>
/// Minimum-length password validator. Length-only follows NIST SP 800-63B 2025
/// guidance: length beats forced complexity. Future plans can add a zxcvbn
/// common-password check.
/// </summary>
public static class PasswordStrength
{
    /// <summary>Minimum required password length in characters.</summary>
    public const int MinimumLength = 8;

    /// <summary>Evaluate the strength of <paramref name="password"/> against the minimum-length rule.</summary>
    public static PasswordStrengthResult Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
        {
            return PasswordStrengthResult.TooShort;
        }
        return PasswordStrengthResult.Strong;
    }
}

/// <summary>Outcome of a <see cref="PasswordStrength.Validate"/> check.</summary>
public enum PasswordStrengthResult
{
    /// <summary>Password meets all requirements.</summary>
    Strong,
    /// <summary>Password is shorter than <see cref="PasswordStrength.MinimumLength"/> characters.</summary>
    TooShort,
}
