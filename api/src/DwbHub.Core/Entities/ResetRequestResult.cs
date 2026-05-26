namespace DwbHub.Core.Entities;

/// <summary>
/// Output of <c>IAuthTokenRepository.IssuePasswordResetTokenAsync</c>. Returned by the
/// single CTE that validates the user and conditionally inserts a new reset token.
/// </summary>
/// <param name="UserFound">True iff a user row matched the supplied email in this tenant.</param>
/// <param name="EmailNotVerified">True iff the user exists but has not yet verified their email.</param>
/// <param name="RateLimited">True iff an unexpired reset token already exists for this user (rate-limit guard).</param>
/// <param name="NewTokenId">ID of the newly inserted token row, or null when no token was created.</param>
public sealed record ResetRequestResult(
    bool UserFound,
    bool EmailNotVerified,
    bool RateLimited,
    long? NewTokenId);
