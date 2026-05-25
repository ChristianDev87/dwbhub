using System.Security.Cryptography;
using System.Text;
using DwbHub.Application.Messaging;

namespace DwbHub.Infrastructure.Messaging;

/// <summary>
/// AES-256-GCM implementation of <see cref="IChannelWebhookCipher"/>.
/// Reuses Plan 0.7's primitive (12-byte nonce, 16-byte tag) but lives in its
/// own Messaging namespace so the dependency graph stays explicit — bot-token
/// and webhook-token encryption don't share an interface today (Plan 1.0
/// spec §3 Option B). Key rotation across both will be Phase 7.
/// </summary>
public sealed class AesGcmChannelWebhookCipher : IChannelWebhookCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int CurrentKeyVersion = 1;

    private readonly byte[] _key;

    public AesGcmChannelWebhookCipher(string base64Key)
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
    public ChannelWebhookEnvelope Encrypt(string webhookToken)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(webhookToken);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        // AesGcm.Encrypt writes the encrypted bytes into the `ciphertext` and `tag`
        // buffers in-place (destination-span pattern). The buffers must be
        // pre-allocated by the caller with the correct sizes.
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
        // AesGcm.Decrypt writes the decrypted bytes into the `plaintextBytes`
        // buffer in-place. Throws CryptographicException if the tag does not match.
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.AuthTag, plaintextBytes);
        return Encoding.UTF8.GetString(plaintextBytes);
    }
}
