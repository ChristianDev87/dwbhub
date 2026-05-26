namespace DwbHub.Api.Controllers.Auth;

/// <summary>Request body for POST /api/auth/password-reset/request.</summary>
/// <param name="TenantSlug">Slug of the tenant the user belongs to.</param>
/// <param name="Email">Email address of the account to reset.</param>
public sealed record PasswordResetRequestRequest(string TenantSlug, string Email);
