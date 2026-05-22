using System.Net;

namespace DwbHub.Application.Auth;

/// <summary>
/// Orchestrates the full login flow: lockout check → tenant lookup → user lookup
/// → BCrypt verify (with timing-attack decoy on miss) → attempt logging → JWT issue.
/// </summary>
public interface ILoginService
{
    Task<LoginOutcome> LoginAsync(string tenantSlug, string email, string password, IPAddress ipAddress, CancellationToken ct = default);
}
