using System.Net;

namespace DwbHub.Application.Setup;

/// <summary>
/// Manages the one-time system setup wizard that creates the first tenant and owner user.
/// After successful completion the wizard is permanently locked via the bootstrap-lock row.
/// </summary>
public interface ISetupService
{
    /// <summary>Return the current setup completion status without side effects.</summary>
    Task<SetupStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Execute the setup wizard: validate the bootstrap token, create the tenant and owner
    /// user, send the verification email, consume the lock, and clean up the token file.
    /// Idempotent via the bootstrap lock — concurrent calls return <see cref="SetupOutcome.AlreadyCompleted"/>.
    /// </summary>
    Task<SetupOutcome> CompleteAsync(SetupRequest request, IPAddress? ip, string? userAgent, CancellationToken ct = default);
}
