using DwbHub.Core.Entities;

namespace DwbHub.Application.Auth;

/// <summary>
/// Discriminated union for the result of a login attempt. The controller
/// pattern-matches on it to produce the correct HTTP response.
/// </summary>
public abstract record LoginOutcome
{
    /// <summary>Login succeeded. Carries the signed access token, the refresh token plaintext, and resolved entities.</summary>
    /// <param name="AccessToken">Short-lived HS256 JWT for API calls.</param>
    /// <param name="RefreshToken">Plaintext refresh token; controller sets as httpOnly cookie.</param>
    /// <param name="User">Authenticated user.</param>
    /// <param name="Tenant">Tenant the user belongs to.</param>
    public sealed record Success(string AccessToken, string RefreshToken, User User, Tenant Tenant) : LoginOutcome;
    /// <summary>Unknown tenant, unknown user, inactive user, or wrong password — all mapped to the same case to prevent enumeration.</summary>
    public sealed record InvalidCredentials : LoginOutcome;
    /// <summary>Too many recent failed attempts from this email/IP combination.</summary>
    /// <param name="RetryAfterSeconds">Seconds until the lockout window expires.</param>
    public sealed record LockedOut(int RetryAfterSeconds) : LoginOutcome;
    /// <summary>Credentials are correct but the email address has not been verified yet.</summary>
    /// <param name="Email">The unverified email address, so the controller can hint the user.</param>
    public sealed record EmailNotVerified(string Email) : LoginOutcome;
}
