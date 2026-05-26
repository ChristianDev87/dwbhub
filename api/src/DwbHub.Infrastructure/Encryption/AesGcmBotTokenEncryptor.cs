using System.Security.Cryptography;
using System.Text;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;

namespace DwbHub.Infrastructure.Encryption;

/// <summary>
/// AES-256-GCM-backed implementation of <see cref="IBotTokenEncryptor"/>.
/// Each call to <see cref="Encrypt"/> generates a fresh 96-bit nonce via
/// <see cref="System.Security.Cryptography.RandomNumberGenerator"/>; the resulting
/// <see cref="DwbHub.Core.Encryption.CipherEnvelope"/> (nonce + ciphertext + 128-bit tag)
/// is the only form persisted to the database.
/// </summary>
public sealed class AesGcmBotTokenEncryptor : IBotTokenEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    /// <summary>
    /// Initialise the encryptor with the project's AES-256 key.
    /// </summary>
    /// <param name="base64Key">Base64-encoded 256-bit key (exactly 32 bytes after decode).</param>
    /// <exception cref="ArgumentException">Thrown when the decoded key is not exactly 32 bytes.</exception>
    public AesGcmBotTokenEncryptor(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
        {
            throw new ArgumentException(
                "DWBHUB_ENCRYPTION_KEY must decode to exactly 32 bytes (AES-256).",
                nameof(base64Key));
        }
    }

    /// <inheritdoc/>
    public CipherEnvelope Encrypt(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        // AesGcm.Encrypt writes the encrypted bytes into the `ciphertext` and `tag`
        // buffers in-place (destination-span pattern). The buffers must be
        // pre-allocated by the caller with the correct sizes.
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        return new CipherEnvelope(nonce, ciphertext, tag);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Throws <see cref="System.Security.Cryptography.CryptographicException"/> if the
    /// GCM authentication tag does not match (tampered ciphertext or wrong key).
    /// </remarks>
    public string Decrypt(CipherEnvelope envelope)
    {
        var plaintextBytes = new byte[envelope.Ciphertext.Length];
        // AesGcm.Decrypt writes the decrypted bytes into the `plaintextBytes`
        // buffer in-place. Throws CryptographicException if the tag does not match.
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintextBytes);
        return Encoding.UTF8.GetString(plaintextBytes);
    }
}
