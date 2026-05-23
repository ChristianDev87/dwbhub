namespace DwbHub.Core.Encryption;

/// <summary>
/// Three-part output of AES-256-GCM authenticated encryption. Carried between
/// the encryptor and the repository as a single value. Treat as max-sensitivity:
/// never log this directly, never include in audit payloads, never serialize
/// to HTTP responses.
/// </summary>
public readonly record struct CipherEnvelope(
    byte[] Nonce,      // 12 bytes for AES-GCM (IV)
    byte[] Ciphertext, // variable, = plaintext length
    byte[] Tag);       // 16 bytes for AES-GCM (auth tag)
