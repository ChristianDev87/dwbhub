namespace DwbHub.Application.Messaging;

/// <summary>
/// AES-GCM encrypted envelope for a Discord webhook token.
/// AES-GCM encrypted envelope for a Discord webhook token. Same 4-tuple shape as <see cref="DwbHub.Core.Encryption.CipherEnvelope"/> (Ciphertext + Nonce + AuthTag + KeyVersion) so a future key-rotation pass can touch both tables uniformly.
/// </summary>
/// <param name="Ciphertext">AES-GCM-encrypted webhook token bytes.</param>
/// <param name="Nonce">96-bit IV used for this encryption; unique per envelope.</param>
/// <param name="AuthTag">128-bit GCM authentication tag; any modification causes decryption to fail.</param>
/// <param name="KeyVersion">Integer key version used for encryption; supports multi-version key rotation.</param>
public sealed record ChannelWebhookEnvelope(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] AuthTag,
    int KeyVersion);
