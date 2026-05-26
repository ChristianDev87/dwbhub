namespace DwbHub.Application.Messaging;

/// <summary>
/// Encrypts + decrypts Discord webhook tokens for storage in channel_webhooks.
/// Uses AES-256-GCM under DWBHUB_ENCRYPTION_KEY (the same key Plan 0.7's
/// bot-token encryptor uses, but via a separate Messaging-namespaced
/// interface — see Plan 1.0 spec §3 Option B for the rationale).
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
