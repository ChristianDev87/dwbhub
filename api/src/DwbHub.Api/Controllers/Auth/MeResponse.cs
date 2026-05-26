namespace DwbHub.Api.Controllers.Auth;

/// <summary>Response body for GET /api/t/{slug}/me — identity and tenant context of the caller.</summary>
/// <param name="UserId">Internal ID of the authenticated user.</param>
/// <param name="TenantId">Internal ID of the resolved tenant.</param>
/// <param name="TenantSlug">URL slug of the resolved tenant.</param>
/// <param name="Role">Role of the user within this tenant.</param>
public sealed record MeResponse(long UserId, long TenantId, string TenantSlug, string Role);
