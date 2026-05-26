namespace DwbHub.Application.Bot;

/// <summary>
/// Result of a manual bot reconnect request. Either the reconnect was triggered,
/// or it was rejected because a cool-down window from a previous manual reconnect
/// on the same guild is still active.
/// </summary>
public abstract record ManualReconnectOutcome
{
    private ManualReconnectOutcome() { }

    /// <summary>Reconnect was accepted and the background task was started.</summary>
    public sealed record Triggered : ManualReconnectOutcome;

    /// <summary>
    /// Rejected because the last manual reconnect for this guild was less than
    /// 60 seconds ago. <paramref name="RetryAfterSeconds"/> is the remaining
    /// wait time (always ≥1, clamped from the elapsed delta).
    /// </summary>
    /// <param name="RetryAfterSeconds">Seconds the caller must wait before retrying.</param>
    public sealed record CoolDownActive(int RetryAfterSeconds) : ManualReconnectOutcome;
}
