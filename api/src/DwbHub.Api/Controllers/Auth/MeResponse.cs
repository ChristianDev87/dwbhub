namespace DwbHub.Api.Controllers.Auth;

public sealed record MeResponse(long UserId, long TenantId, string TenantSlug, string Role);
