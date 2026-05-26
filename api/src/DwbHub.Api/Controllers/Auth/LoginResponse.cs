namespace DwbHub.Api.Controllers.Auth;

/// <summary>Response body for a successful login.</summary>
/// <param name="AccessToken">Short-lived JWT bearer token.</param>
/// <param name="User">Authenticated user summary.</param>
/// <param name="Tenant">Tenant the user authenticated against.</param>
public sealed record LoginResponse(
    string AccessToken,
    LoginUserDto User,
    LoginTenantDto Tenant);

/// <summary>Authenticated user fields included in the login response.</summary>
/// <param name="Id">Internal user ID.</param>
/// <param name="Email">User email address.</param>
/// <param name="DisplayName">User display name.</param>
/// <param name="Role">User role string (e.g. <c>Owner</c>, <c>Member</c>).</param>
public sealed record LoginUserDto(long Id, string Email, string DisplayName, string Role);

/// <summary>Tenant fields included in the login response.</summary>
/// <param name="Id">Internal tenant ID.</param>
/// <param name="Slug">URL slug identifying the tenant.</param>
/// <param name="Name">Human-readable tenant name.</param>
public sealed record LoginTenantDto(long Id, string Slug, string Name);
