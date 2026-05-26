using DwbHub.Application.Auth;

namespace DwbHub.Infrastructure.Auth;

/// <summary>
/// BCrypt password hashing with work factor 12 (per briefing). VerifyDecoy
/// runs the same BCrypt cost against a fixed dummy hash so unknown-user
/// requests take the same wall-clock time as wrong-password requests.
/// </summary>
public sealed class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 12;

    // Pre-computed BCrypt hash of the decoy plaintext. Cracking it reveals no real user credential.
    private const string DummyHash = "$2a$12$dSgI7slUjNUWjUm7XbF3juhWzgFXPKcIBUxHn6OrkNCKzQlRPSUtW";

    /// <inheritdoc/>
    public string Hash(string plain)
    {
        return BCrypt.Net.BCrypt.HashPassword(plain, WorkFactor);
    }

    /// <inheritdoc/>
    public bool Verify(string plain, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(plain, hash);
    }

    /// <inheritdoc/>
    public void VerifyDecoy(string plain)
    {
        _ = BCrypt.Net.BCrypt.Verify(plain, DummyHash);
    }
}
