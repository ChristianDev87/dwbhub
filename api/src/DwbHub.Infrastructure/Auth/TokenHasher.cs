using System.Security.Cryptography;
using System.Text;
using DwbHub.Application.Auth;

namespace DwbHub.Infrastructure.Auth;

/// <summary>
/// SHA-256-backed implementation of <see cref="ITokenHasher"/>.
/// </summary>
public sealed class TokenHasher : ITokenHasher
{
    /// <inheritdoc/>
    public byte[] Hash(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return SHA256.HashData(bytes);
    }
}
