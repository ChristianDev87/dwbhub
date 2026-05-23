using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;

namespace DwbHub.Tests.Integration.Bot;

/// <summary>
/// Test-only IBotTokenEncryptor that always returns a preset token on Decrypt,
/// bypassing real AES-GCM so FakeEnvelope bytes don't cause CryptographicException
/// when BotConnectionManager calls Decrypt in ReconnectOneAsync.
/// </summary>
public sealed class FakeBotTokenEncryptor : IBotTokenEncryptor
{
    public const string FakePlaintext = "FAKE_TOKEN_FOR_TESTING_ONLY";

    public CipherEnvelope Encrypt(string plaintext) => new(
        Nonce: Enumerable.Repeat((byte)0xAA, 12).ToArray(),
        Ciphertext: Enumerable.Repeat((byte)0xBB, 80).ToArray(),
        Tag: Enumerable.Repeat((byte)0xCC, 16).ToArray());

    public string Decrypt(CipherEnvelope envelope) => FakePlaintext;
}
