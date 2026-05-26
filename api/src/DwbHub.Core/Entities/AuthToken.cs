using System.Net;

namespace DwbHub.Core.Entities;

/// <summary>
/// One row in auth_tokens. Tokens are single-use: ConsumedAt != null means
/// the token has been redeemed or invalidated.
/// </summary>
/// <param name="Id">Internal primary key.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="UserId">User this token was issued for.</param>
/// <param name="Email">Email address captured at issue time (denormalised for query convenience).</param>
/// <param name="Purpose">Discriminator indicating whether this is an email-verify or password-reset token.</param>
/// <param name="TokenHash">SHA-256 hash of the plaintext token sent to the user's email.</param>
/// <param name="CreatedAt">Timestamp when the token row was inserted.</param>
/// <param name="ExpiresAt">Timestamp after which the token is no longer valid.</param>
/// <param name="ConsumedAt">Timestamp of first use, or null if still unused.</param>
/// <param name="IpAddress">Client IP at issue time, or null.</param>
/// <param name="UserAgent">HTTP User-Agent header at issue time, or null.</param>
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
