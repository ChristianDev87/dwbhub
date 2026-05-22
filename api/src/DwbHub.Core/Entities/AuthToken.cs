using System.Net;

namespace DwbHub.Core.Entities;

/// <summary>
/// One row in auth_tokens. Tokens are single-use: ConsumedAt != null means
/// the token has been redeemed or invalidated.
/// </summary>
public sealed record AuthToken(
    long Id,
    long TenantId,
    long UserId,
    string Email,
    AuthTokenPurpose Purpose,
    byte[] TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConsumedAt,
    IPAddress? IpAddress,
    string? UserAgent);
