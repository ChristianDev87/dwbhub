namespace DwbHub.Api.Controllers.Auth;

/// <summary>Request body for POST /api/auth/verify-email/resend.</summary>
/// <param name="TenantSlug">Slug of the tenant the user belongs to.</param>
/// <param name="Email">Email address to resend the verification message to.</param>
public sealed record VerifyEmailResendRequest(string TenantSlug, string Email);
