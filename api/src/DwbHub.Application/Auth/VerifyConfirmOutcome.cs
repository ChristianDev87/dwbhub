namespace DwbHub.Application.Auth;

/// <summary>
/// Discriminated union for the result of an email verification confirmation attempt.
/// </summary>
public abstract record VerifyConfirmOutcome
{
    /// <summary>Token was valid and the email address is now marked as verified.</summary>
    public sealed record Success : VerifyConfirmOutcome;
    /// <summary>Token not found, already consumed, or expired.</summary>
    public sealed record Invalid : VerifyConfirmOutcome;
}
