namespace DwbHub.Application.Auth;

/// <summary>
/// Discriminated union for the result of a refresh attempt. The controller
/// pattern-matches on it to produce the correct HTTP response + cookie behavior.
/// </summary>
public abstract record RefreshOutcome
{
    /// <summary>Token rotated successfully. Carry the new access JWT and fresh refresh token to the controller.</summary>
    /// <param name="AccessToken">Freshly issued short-lived HS256 JWT.</param>
    /// <param name="RefreshToken">New refresh token plaintext; controller sets as httpOnly cookie.</param>
    public sealed record Success(string AccessToken, string RefreshToken) : RefreshOutcome;
    /// <summary>Token not found, already expired, or structurally invalid.</summary>
    public sealed record Invalid : RefreshOutcome;
    /// <summary>The user's role or active status changed since the token was issued; force re-login.</summary>
    public sealed record RightsChanged : RefreshOutcome;
    /// <summary>Replay of a previously-revoked token detected; entire forward chain has been revoked.</summary>
    public sealed record ChainCompromised : RefreshOutcome;
}
