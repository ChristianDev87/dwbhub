using System.Net;

namespace DwbHub.Core.Repositories;

/// <summary>
/// System-wide login attempt log (allowlisted in check-tenant-filter.ps1).
/// </summary>
public interface ILoginAttemptRepository
{
    Task RecordAsync(string email, IPAddress ipAddress, bool success, CancellationToken ct = default);
    Task<int> CountFailedSinceAsync(string email, IPAddress ipAddress, DateTimeOffset since, CancellationToken ct = default);
}
