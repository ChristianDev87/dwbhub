namespace DwbHub.Api.Controllers.Auth;

/// <summary>Request body for POST /api/auth/password-reset/confirm.</summary>
/// <param name="Token">One-time reset token from the password-reset email.</param>
/// <param name="NewPassword">Desired new password in plaintext. Transmitted over TLS only.</param>
public sealed record PasswordResetConfirmRequest(string Token, string NewPassword);
