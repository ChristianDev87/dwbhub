namespace DwbHub.Application.Auth;

/// <summary>
/// BCrypt-backed password hashing + verification.
/// `VerifyDecoy` runs the same BCrypt cost against a fixed dummy hash so
/// timing of the unknown-user branch matches the wrong-password branch.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hash a plaintext password and return the BCrypt hash string.</summary>
    string Hash(string plain);
    /// <summary>Verify a plaintext password against a stored BCrypt hash.</summary>
    bool Verify(string plain, string hash);
    /// <summary>
    /// Run a full BCrypt verify against a fixed dummy hash to normalise the
    /// timing of the unknown-user path against the wrong-password path.
    /// </summary>
    void VerifyDecoy(string plain);
}
