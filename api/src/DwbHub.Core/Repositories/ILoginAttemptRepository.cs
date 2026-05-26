using System.Net;

namespace DwbHub.Core.Repositories;

/// <summary>
/// System-wide login attempt log (allowlisted in check-tenant-filter.ps1).
/// </summary>
public interface ILoginAttemptRepository
{
    /// <summary>Append one login attempt row for the given email and IP address.</summary>
    Task RecordAsync(string email, IPAddress ipAddress, bool success, CancellationToken ct = default);

    /// <summary>
    /// Count failed login attempts for a (email, IP) pair since the given timestamp.
    /// Used by the rate-limiter to gate repeated failures before issuing a JWT.
    /// </summary>
    Task<int> CountFailedSinceAsync(string email, IPAddress ipAddress, DateTimeOffset since, CancellationToken ct = default);
}
