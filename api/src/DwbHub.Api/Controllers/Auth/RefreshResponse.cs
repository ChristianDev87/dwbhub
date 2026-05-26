namespace DwbHub.Api.Controllers.Auth;

/// <summary>Response body for a successful token refresh.</summary>
/// <param name="AccessToken">New short-lived JWT bearer token.</param>
public sealed record RefreshResponse(string AccessToken);
