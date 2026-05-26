namespace DwbHub.Api.Controllers.Auth;

/// <summary>Request body for POST /api/auth/verify-email/confirm.</summary>
/// <param name="Token">One-time verification token from the verification email.</param>
public sealed record VerifyEmailConfirmRequest(string Token);
