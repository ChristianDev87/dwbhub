namespace DwbHub.Core.Entities;

/// <summary>
/// Output of <c>IAuthTokenRepository.ConfirmPasswordResetAsync</c>. Returned by the
/// single CTE that validates and redeems a password-reset token. Null flags indicate
/// that the preceding check could not be evaluated (e.g. token not found).
/// </summary>
/// <param name="TokenFound">True iff a row matched the input hash.</param>
/// <param name="NotConsumed">True iff the matched token has not been consumed yet. Null if no row.</param>
/// <param name="NotExpired">True iff <c>expires_at &gt; now()</c>. Null if no row.</param>
/// <param name="Updated">True iff the password hash was successfully updated.</param>
/// <param name="SessionsRevoked">Number of refresh-token rows revoked as part of the password change.</param>
public sealed record ResetConfirmResult(
    bool TokenFound,
    bool? NotConsumed,
    bool? NotExpired,
    bool Updated,
    int SessionsRevoked);
