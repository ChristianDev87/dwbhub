namespace DwbHub.Application.Background;

/// <summary>
/// Hangfire job that removes expired and consumed auth tokens from the auth_tokens table.
/// Keeps the table bounded and avoids index bloat on the token_hash lookup column.
/// </summary>
public interface IAuthTokenPruneJob
{
    /// <summary>Delete expired and already-consumed auth token rows.</summary>
    Task RunAsync(CancellationToken ct = default);
}
