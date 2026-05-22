namespace DwbHub.Application.Auth;

/// <summary>
/// Cryptographically-random bytes encoded as URL-safe Base64. Default 32-byte
/// inputs yield 43-char outputs with no padding — fit nicely into an httpOnly
/// cookie value without URL-encoding overhead.
/// </summary>
public interface ITokenGenerator
{
    string GenerateUrlSafeBase64(int byteCount = 32);
}
