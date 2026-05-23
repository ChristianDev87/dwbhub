namespace DwbHub.Core.Entities;

/// <summary>
/// Encrypted bot-token envelope stored at-rest for a guild. The three byte arrays
/// together form an AES-256-GCM ciphertext. None of these fields should ever be
/// logged or returned in API responses; the only legitimate consumers are
/// IBotTokenEncryptor.Decrypt (Plan 0.8 BotConnectionManager).
/// </summary>
public sealed record GuildBotCredential(
    long Id,
    long GuildId,
    long TenantId,
    byte[] Nonce,       // 12 bytes (AES-GCM IV)
    byte[] Ciphertext,  // variable, = plaintext length
    byte[] Tag,         // 16 bytes (AES-GCM auth tag)
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
