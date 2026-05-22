using System.Net;
using DwbHub.Core.Entities;

namespace DwbHub.Application.Auth;

public interface IRefreshTokenService
{
    /// <summary>
    /// Called by LoginService on the Success branch. Generates plaintext, hashes
    /// it, inserts the refresh-token row with the user's current role+is_active
    /// as snapshot, returns the plaintext for the controller to set as cookie.
    /// </summary>
    Task<string> IssueForLoginAsync(User user, Tenant tenant, IPAddress? ip, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Rotates the supplied refresh-token plaintext. On Success: mints a fresh
    /// access JWT, inserts a new refresh-token, revokes the old. On failure:
    /// returns the appropriate outcome and (for ChainCompromised + RightsChanged)
    /// revokes either the chain or the single token.
    /// </summary>
    Task<RefreshOutcome> RefreshAsync(string refreshTokenPlaintext, IPAddress? ip, string? userAgent, CancellationToken ct = default);

    /// <summary>Revoke the supplied refresh-token (idempotent — no error if already revoked or unknown).</summary>
    Task LogoutAsync(string refreshTokenPlaintext, CancellationToken ct = default);
}
