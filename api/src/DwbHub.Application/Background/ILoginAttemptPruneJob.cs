namespace DwbHub.Application.Background;

/// <summary>
/// Hangfire job that removes login attempt rows older than the lockout window from
/// login_attempts, keeping the table bounded and the failed-count query fast.
/// </summary>
public interface ILoginAttemptPruneJob
{
    /// <summary>Delete login attempt rows that are no longer relevant for lockout calculations.</summary>
    Task RunAsync(CancellationToken ct = default);
}
