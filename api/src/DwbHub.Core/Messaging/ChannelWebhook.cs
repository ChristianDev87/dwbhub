namespace DwbHub.Core.Messaging;

/// <summary>
/// Per-bridged-channel encrypted Discord webhook credentials.
/// Token shape: AES-256-GCM envelope (ciphertext + nonce + auth_tag + key_version),
/// Token shape is an AES-256-GCM envelope (ciphertext + nonce + auth_tag + key_version), matching guild_bot_credentials for uniform future key rotation.
/// The byte arrays must never be logged or included in API responses — the only
/// The only legitimate consumer is <see cref="DwbHub.Application.Messaging.IChannelWebhookCipher.Decrypt"/>.
/// </summary>
public sealed record ChannelWebhook
{
    public long Id { get; init; }
    public long TenantId { get; init; }
    public long ChannelId { get; init; }
    public long DiscordWebhookId { get; init; }
    public byte[] Ciphertext { get; init; } = Array.Empty<byte>();
    public byte[] Nonce { get; init; } = Array.Empty<byte>();
    public byte[] AuthTag { get; init; } = Array.Empty<byte>();
    public int KeyVersion { get; init; } = 1;
    public long CreatedByUserId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
