namespace DwbHub.Application.Auth;

/// <summary>
/// Discriminated union for the result of a refresh attempt. The controller
/// pattern-matches on it to produce the correct HTTP response + cookie behavior.
/// </summary>
public abstract record RefreshOutcome
{
    public sealed record Success(string AccessToken, string RefreshToken) : RefreshOutcome;
    public sealed record Invalid                                          : RefreshOutcome;
    public sealed record RightsChanged                                    : RefreshOutcome;
    public sealed record ChainCompromised                                 : RefreshOutcome;
}
