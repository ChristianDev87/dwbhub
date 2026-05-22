using DwbHub.Core.Entities;

namespace DwbHub.Application.Auth;

/// <summary>
/// Issues HS256 access tokens signed with DWBHUB_JWT_SECRET.
/// Lifetime is 24 hours (per briefing §6.1 / Spec 0 §5).
/// </summary>
public interface IJwtIssuer
{
    string Issue(User user, Tenant tenant);
}
