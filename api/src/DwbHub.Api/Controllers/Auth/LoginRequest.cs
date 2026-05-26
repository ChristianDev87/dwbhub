namespace DwbHub.Api.Controllers.Auth;

/// <summary>Request body for POST /api/tenants/{slug}/auth/login.</summary>
/// <param name="Email">User email address.</param>
/// <param name="Password">Plaintext password. Transmitted over TLS only.</param>
public sealed record LoginRequest(string Email, string Password);
