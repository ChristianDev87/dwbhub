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

    // Pre-computed BCrypt hash of "DWBHUB_DECOY_PASSWORD_DO_NOT_USE" at work factor 12.
    // Cracking this reveals only the dummy plaintext, which no real user has.
    private const string DummyHash = "$2a$12$dSgI7slUjNUWjUm7XbF3juhWzgFXPKcIBUxHn6OrkNCKzQlRPSUtW";

    public string Hash(string plain)
    {
        return BCrypt.Net.BCrypt.HashPassword(plain, WorkFactor);
    }

    public bool Verify(string plain, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(plain, hash);
    }

    public void VerifyDecoy(string plain)
    {
        // Discard the result — we just want the BCrypt work time to elapse.
        _ = BCrypt.Net.BCrypt.Verify(plain, DummyHash);
    }
}
