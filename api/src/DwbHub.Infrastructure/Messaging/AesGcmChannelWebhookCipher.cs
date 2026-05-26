using System.Security.Cryptography;
using System.Text;
using DwbHub.Application.Messaging;

namespace DwbHub.Infrastructure.Messaging;

/// <summary>
/// AES-256-GCM implementation of <see cref="IChannelWebhookCipher"/>. Same primitive as <see cref="AesGcmBotTokenEncryptor"/> (12-byte nonce, 16-byte tag) but in a separate Messaging namespace — the two encryption surfaces don't share an interface. Key rotation across both is deferred.
/// </summary>
public sealed class AesGcmChannelWebhookCipher : IChannelWebhookCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int CurrentKeyVersion = 1;

    private readonly byte[] _key;

    public AesGcmChannelWebhookCipher(string base64Key)
    {
        // Guard empty/whitespace before Base64 decode so operators get ArgumentException, not FormatException.
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new ArgumentException(
                "DWBHUB_ENCRYPTION_KEY must not be null, empty, or whitespace.",
                nameof(base64Key));
        }

        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
        {
            throw new ArgumentException(
                "DWBHUB_ENCRYPTION_KEY must decode to exactly 32 bytes (AES-256).",
                nameof(base64Key));
        }
    }

    /// <inheritdoc/>
    public ChannelWebhookEnvelope Encrypt(string webhookToken)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(webhookToken);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        // Encrypt writes ciphertext + tag in-place; both buffers must be pre-allocated.
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        return new ChannelWebhookEnvelope(ciphertext, nonce, tag, CurrentKeyVersion);
    }

    /// <inheritdoc/>
    public string Decrypt(ChannelWebhookEnvelope envelope)
    {
        if (envelope.KeyVersion != CurrentKeyVersion)
        {
            throw new InvalidOperationException(
                $"unsupported key version {envelope.KeyVersion}; only version {CurrentKeyVersion} is supported today. Key rotation: Phase 7.");
        }

        var plaintextBytes = new byte[envelope.Ciphertext.Length];
        // Decrypt writes plaintext in-place; CryptographicException on tag mismatch.
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.AuthTag, plaintextBytes);
        return Encoding.UTF8.GetString(plaintextBytes);
    }
}
