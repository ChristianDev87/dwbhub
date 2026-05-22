namespace DwbHub.Api.Controllers.Auth;

public sealed record PasswordResetConfirmRequest(string Token, string NewPassword);
