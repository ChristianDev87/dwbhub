namespace DwbHub.Core.Entities;

/// <summary>
/// Output of <c>IAuthTokenRepository.IssueEmailVerifyTokenAsync</c>. Returned by the
/// single CTE that validates the user and conditionally inserts a new verification token.
/// </summary>
/// <param name="UserFound">True iff a user row matched the supplied email in this tenant.</param>
/// <param name="AlreadyVerified">True iff the user exists but already has <c>email_verified_at IS NOT NULL</c>.</param>
/// <param name="RateLimited">True iff an unexpired verification token already exists for this user (rate-limit guard).</param>
/// <param name="NewTokenId">ID of the newly inserted token row, or null when no token was created.</param>
public sealed record VerifyResendResult(
    bool UserFound,
    bool AlreadyVerified,
    bool RateLimited,
    long? NewTokenId);
