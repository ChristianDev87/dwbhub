using DwbHub.Core.Entities;

namespace DwbHub.Application.Auth;

/// <summary>
/// Discriminated union for the result of a login attempt. The controller
/// pattern-matches on it to produce the correct HTTP response.
/// </summary>
public abstract record LoginOutcome
{
    public sealed record Success(string AccessToken, string RefreshToken, User User, Tenant Tenant) : LoginOutcome;
    public sealed record InvalidCredentials : LoginOutcome;
    public sealed record LockedOut(int RetryAfterSeconds) : LoginOutcome;
    public sealed record EmailNotVerified(string Email) : LoginOutcome;
}
