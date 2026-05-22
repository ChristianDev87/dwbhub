namespace DwbHub.Api.Controllers.Auth;

public sealed record LoginResponse(
    string AccessToken,
    LoginUserDto User,
    LoginTenantDto Tenant);

public sealed record LoginUserDto(long Id, string Email, string DisplayName, string Role);
public sealed record LoginTenantDto(long Id, string Slug, string Name);
