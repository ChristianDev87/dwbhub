using System.Net;

namespace DwbHub.Application.Auth;

/// <summary>
/// Manages email address verification for newly-created user accounts.
/// </summary>
public interface IEmailVerificationService
{
    /// <summary>
    /// Issue (or re-issue) a verification token and send the verification email.
    /// Silent no-op when the address is unknown, already verified, or rate-limited —
    /// callers should not branch on the outcome — branching would allow user enumeration.
    /// </summary>
    Task ResendAsync(string tenantSlug, string email, string locale, IPAddress? ip, string? userAgent, CancellationToken ct = default);

    /// <summary>
    /// Consume a verification token and mark the email address as verified.
    /// Returns <see cref="VerifyConfirmOutcome.Invalid"/> for any token problem
    /// (not found, already used, expired) without distinguishing the cause.
    /// </summary>
    Task<VerifyConfirmOutcome> ConfirmAsync(string tokenPlaintext, CancellationToken ct = default);
}
