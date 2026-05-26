using System.Net;

namespace DwbHub.Application.Auth;

/// <summary>
/// Handles the password reset flow: token issuance, email delivery, and confirmation.
/// </summary>
public interface IPasswordResetService
{
    /// <summary>
    /// Issue a password reset token and send the reset email to the given address.
    /// Silent no-op when the address is unknown, email not verified, or rate-limited —
    /// callers should not branch on the outcome — branching would allow user enumeration.
    /// </summary>
    Task RequestAsync(string tenantSlug, string email, string locale, IPAddress? ip, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Consume a password reset token and update the user's password hash.
    /// Returns <see cref="ResetConfirmOutcome.WeakPassword"/> before touching the DB
    /// if the new password is too short. Returns <see cref="ResetConfirmOutcome.Invalid"/>
    /// for any token problem (not found, already used, expired).
    /// </summary>
    Task<ResetConfirmOutcome> ConfirmAsync(string tokenPlaintext, string newPassword, CancellationToken ct = default);
}
