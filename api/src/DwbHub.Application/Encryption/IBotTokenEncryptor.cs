using DwbHub.Core.Encryption;

namespace DwbHub.Application.Encryption;

/// <summary>
/// Encrypts and decrypts Discord bot tokens with AES-256-GCM. The implementation
/// holds the master key (loaded once from <c>DWBHUB_ENCRYPTION_KEY</c> env var)
/// and is registered as a singleton; each call constructs a fresh <c>AesGcm</c>
/// instance internally so concurrent calls are safe.
/// </summary>
public interface IBotTokenEncryptor
{
    /// <summary>
    /// Encrypts a plaintext bot token. Each call generates a fresh 12-byte nonce
    /// (cryptographically random). The returned envelope is suitable for at-rest
    /// storage. Plaintext is UTF-8 encoded.
    /// </summary>
    CipherEnvelope Encrypt(string plaintext);

    /// <summary>
    /// Decrypts a previously-encrypted bot token. Throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/>
    /// if the authentication tag does not match (indicates either tampered
    /// ciphertext or a wrong key — callers should not continue with bot
    /// connection in that case).
    /// </summary>
    string Decrypt(CipherEnvelope envelope);
}
