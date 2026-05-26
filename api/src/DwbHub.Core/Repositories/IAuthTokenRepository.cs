using System.Net;
using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Single-use auth-token storage covering email verification and password-reset
/// flows. Each operation is implemented as a single CTE so validation and mutation
/// happen atomically — no TOCTOU window between the token lookup and the update.
/// </summary>
public interface IAuthTokenRepository
{
    /// <summary>
    /// Issue an email-verification token. Validates the user exists and is not
    /// already verified or rate-limited, then inserts a new token row.
    /// Returns diagnostic flags the caller uses to derive the service outcome.
    /// </summary>
    Task<VerifyResendResult> IssueEmailVerifyTokenAsync(
        long tenantId, string email, byte[] tokenHash, DateTimeOffset expiresAt,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default);

    /// <summary>
    /// Consume an email-verification token. Validates the token hash, marks it
    /// consumed, and sets <c>email_verified_at</c> on the user row — all in one
    /// CTE. Returns diagnostic flags the caller uses to derive the service outcome.
    /// </summary>
    Task<VerifyConfirmResult> ConfirmEmailVerifyAsync(
        byte[] tokenHash,
        CancellationToken ct = default);

    /// <summary>
    /// Issue a password-reset token. Validates the user exists, has a verified email,
    /// and is not rate-limited, then inserts a new token row. Returns diagnostic flags
    /// the caller uses to derive the service outcome.
    /// </summary>
    Task<ResetRequestResult> IssuePasswordResetTokenAsync(
        long tenantId, string email, byte[] tokenHash, DateTimeOffset expiresAt,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default);

    /// <summary>
    /// Consume a password-reset token. Validates the token hash, marks it consumed,
    /// updates the user's password hash, and revokes all existing refresh tokens for
    /// that user — all in one CTE. Returns diagnostic flags the caller uses to derive
    /// the service outcome.
    /// </summary>
    Task<ResetConfirmResult> ConfirmPasswordResetAsync(
        byte[] tokenHash, string newPasswordHash,
        CancellationToken ct = default);
}
