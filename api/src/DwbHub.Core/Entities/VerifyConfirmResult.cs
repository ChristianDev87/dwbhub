namespace DwbHub.Core.Entities;

/// <summary>
/// Output of <c>IAuthTokenRepository.ConfirmEmailVerifyAsync</c>. Returned by the
/// single CTE that validates and redeems an email-verification token. Null flags
/// indicate that the preceding check could not be evaluated (e.g. token not found).
/// </summary>
/// <param name="TokenFound">True iff a row matched the input hash.</param>
/// <param name="NotConsumed">True iff the matched token has not been consumed yet. Null if no row.</param>
/// <param name="NotExpired">True iff <c>expires_at &gt; now()</c>. Null if no row.</param>
/// <param name="Verified">True iff <c>email_verified_at</c> was set on the user row.</param>
public sealed record VerifyConfirmResult(
    bool TokenFound,
    bool? NotConsumed,
    bool? NotExpired,
    bool Verified);
