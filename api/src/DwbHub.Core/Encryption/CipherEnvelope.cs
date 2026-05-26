namespace DwbHub.Core.Encryption;

/// <summary>
/// Three-part output of AES-256-GCM authenticated encryption. Carried between
/// the encryptor and the repository as a single value. Treat as max-sensitivity:
/// never log this directly, never include in audit payloads, never serialize
/// to HTTP responses.
/// </summary>
/// <param name="Nonce">96-bit (12-byte) IV. Must be unique per (key, plaintext) pair.</param>
/// <param name="Ciphertext">AES-256-GCM-encrypted payload. Length equals the plaintext length.</param>
/// <param name="Tag">128-bit (16-byte) GCM authentication tag.</param>
public readonly record struct CipherEnvelope(
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag);
