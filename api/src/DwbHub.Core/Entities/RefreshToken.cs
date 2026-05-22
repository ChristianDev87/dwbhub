using System.Net;

namespace DwbHub.Core.Entities;

/// <summary>
/// One row in refresh_tokens. The `IssuedRole` + `IssuedWasActive` pair captures
/// the user's state at issue time; rotation compares this snapshot against the
/// user's current state and forces re-login on any mismatch.
/// </summary>
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
