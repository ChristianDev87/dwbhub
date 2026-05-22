namespace DwbHub.Core.Entities;

/// <summary>
/// Discriminator for auth_tokens rows. Stored as TEXT in the DB to match the CHECK
/// constraint values: 'email_verify' or 'password_reset'.
/// </summary>
public enum AuthTokenPurpose
{
    EmailVerify,
    PasswordReset,
}
