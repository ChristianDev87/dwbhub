using System.Security.Cryptography;
using System.Text;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;

namespace DwbHub.Infrastructure.Encryption;

public sealed class AesGcmBotTokenEncryptor : IBotTokenEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

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
