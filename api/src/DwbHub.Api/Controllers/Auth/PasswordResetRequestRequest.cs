namespace DwbHub.Api.Controllers.Auth;

public sealed record PasswordResetRequestRequest(string TenantSlug, string Email);
