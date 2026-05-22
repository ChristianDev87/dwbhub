namespace DwbHub.Api.Controllers.Auth;

public sealed record VerifyEmailResendRequest(string TenantSlug, string Email);
