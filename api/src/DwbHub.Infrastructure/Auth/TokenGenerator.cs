using System.Security.Cryptography;
using DwbHub.Application.Auth;

namespace DwbHub.Infrastructure.Auth;

public sealed class TokenGenerator : ITokenGenerator
{
    public string GenerateUrlSafeBase64(int byteCount = 32)
    {
        Span<byte> buffer = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(buffer);
        // Base64Url encoding: no padding, `+` → `-`, `/` → `_`.
        return Convert.ToBase64String(buffer)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
