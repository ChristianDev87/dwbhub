namespace DwbHub.Application.Messaging;

/// <summary>
/// AES-GCM encrypted envelope for a Discord webhook token.
/// Shape mirrors Plan 0.7's CipherEnvelope (4-tuple: Ciphertext + Nonce + AuthTag + KeyVersion)
/// so a future key-rotation plan (Phase 7) can touch both tables uniformly.
/// </summary>
public sealed record ChannelWebhookEnvelope(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] AuthTag,
    int KeyVersion);
