namespace DwbHub.Application.Auth;

/// <summary>
/// Deterministic SHA-256 hash for refresh-token plaintext. The plaintext only
/// ever lives in the dwbhub_refresh httpOnly cookie; only its hash is stored in
/// refresh_tokens.token_hash. Output is 32 bytes (256 bits).
/// </summary>
public interface ITokenHasher
{
    byte[] Hash(string plaintext);
}
