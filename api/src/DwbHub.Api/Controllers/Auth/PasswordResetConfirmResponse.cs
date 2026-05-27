namespace DwbHub.Api.Controllers.Auth;

/// <summary>Response body for a successful POST /api/auth/password-reset/confirm.</summary>
/// <param name="Reset">Always <c>true</c> on success.</param>
/// <param name="SessionsRevoked">Number of active sessions that were revoked.</param>
public sealed record PasswordResetConfirmResponse(bool Reset, int SessionsRevoked);
