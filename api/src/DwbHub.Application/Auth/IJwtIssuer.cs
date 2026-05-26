using DwbHub.Core.Entities;

namespace DwbHub.Application.Auth;

/// <summary>
/// Issues HS256 access tokens signed with DWBHUB_JWT_SECRET.
/// Access token lifetime is 24 hours.
/// </summary>
public interface IJwtIssuer
{
    /// <summary>Mint a signed access JWT for the given user and tenant.</summary>
    string Issue(User user, Tenant tenant);
}
