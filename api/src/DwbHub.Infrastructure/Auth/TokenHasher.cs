using System.Security.Cryptography;
using System.Text;
using DwbHub.Application.Auth;

namespace DwbHub.Infrastructure.Auth;

public sealed class TokenHasher : ITokenHasher
{
    public byte[] Hash(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return SHA256.HashData(bytes);
    }
}
