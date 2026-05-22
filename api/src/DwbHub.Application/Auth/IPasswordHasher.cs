namespace DwbHub.Application.Auth;

/// <summary>
/// BCrypt-backed password hashing + verification.
/// `VerifyDecoy` runs the same BCrypt cost against a fixed dummy hash so
/// timing of the unknown-user branch matches the wrong-password branch.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string plain);
    bool Verify(string plain, string hash);
    void VerifyDecoy(string plain);
}
