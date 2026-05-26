namespace DwbHub.Application.Auth;

/// <summary>
/// Discriminated union for the result of a password-reset confirmation attempt.
/// </summary>
public abstract record ResetConfirmOutcome
{
    /// <summary>Password updated successfully.</summary>
    /// <param name="SessionsRevoked">Number of active refresh-token sessions that were invalidated.</param>
    public sealed record Success(int SessionsRevoked) : ResetConfirmOutcome;
    /// <summary>Token not found, already consumed, or expired.</summary>
    public sealed record Invalid : ResetConfirmOutcome;
    /// <summary>New password does not meet the minimum strength requirement.</summary>
    public sealed record WeakPassword : ResetConfirmOutcome;
}
