namespace DwbHub.Api.Controllers.Auth;

/// <summary>Response body for a successful POST /api/auth/verify-email/confirm.</summary>
/// <param name="Verified">Always <c>true</c> on success.</param>
public sealed record VerifyEmailConfirmResponse(bool Verified);
