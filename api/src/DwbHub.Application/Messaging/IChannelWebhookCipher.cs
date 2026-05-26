namespace DwbHub.Application.Messaging;

/// <summary>
/// Encrypts + decrypts Discord webhook tokens for storage in channel_webhooks.
/// Uses AES-256-GCM under DWBHUB_ENCRYPTION_KEY — the same key as <see cref="IBotTokenEncryptor"/>, but via a separate Messaging-namespaced interface so the two encryption concerns don't share a type.
/// </summary>
public interface IChannelWebhookCipher
{
    /// <summary>
    /// Encrypts a webhook token. Each call generates a fresh nonce, so
    /// re-encrypting the same plaintext yields different ciphertexts —
    /// preventing replay correlation in the DB.
    /// </summary>
    ChannelWebhookEnvelope Encrypt(string webhookToken);

    /// <summary>
    /// Decrypts an envelope back to the raw webhook token.
    /// Throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// if the auth tag does not verify (any tamper — ciphertext, nonce, or tag).
    /// Throws <see cref="System.InvalidOperationException"/> if the envelope's
    /// KeyVersion is unsupported (multi-version key support: Phase 7).
    /// </summary>
    string Decrypt(ChannelWebhookEnvelope envelope);
}
