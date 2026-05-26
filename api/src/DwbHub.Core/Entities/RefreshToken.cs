using System.Net;

namespace DwbHub.Core.Entities;

/// <summary>
/// One row in refresh_tokens. The `IssuedRole` + `IssuedWasActive` pair captures
/// the user's state at issue time; rotation compares this snapshot against the
/// user's current state and forces re-login on any mismatch.
/// </summary>
/// <param name="Id">Internal primary key.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="UserId">User who owns this token.</param>
/// <param name="TokenHash">SHA-256 hash of the plaintext token sent to the client.</param>
/// <param name="IssuedAt">Timestamp when the token was created.</param>
/// <param name="ExpiresAt">Timestamp after which the token is no longer valid.</param>
/// <param name="RevokedAt">Timestamp of revocation, or null when the token is still active.</param>
/// <param name="IssuedRole">User's role at the time this token was issued (snapshot for drift detection).</param>
/// <param name="IssuedWasActive">Whether the user was active when this token was issued (snapshot for drift detection).</param>
/// <param name="ReplacedByTokenId">ID of the successor token row, set during rotation. Null for the current token.</param>
/// <param name="UserAgent">HTTP User-Agent header captured at issue time, or null.</param>
/// <param name="IpAddress">Client IP at issue time, or null.</param>
public sealed record RefreshToken(
    long Id,
    long TenantId,
    long UserId,
    byte[] TokenHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    UserRole IssuedRole,
    bool IssuedWasActive,
    long? ReplacedByTokenId,
    string? UserAgent,
    IPAddress? IpAddress);
