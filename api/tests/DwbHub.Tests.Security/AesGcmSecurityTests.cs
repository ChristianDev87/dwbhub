using System.Security.Cryptography;
using DwbHub.Core.Encryption;
using DwbHub.Infrastructure.Encryption;
using FluentAssertions;

namespace DwbHub.Tests.Security;

public sealed class AesGcmSecurityTests
{
    // 32 zero bytes, Base64-encoded — only for tests.
    private const string TestKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    // A realistic bot-token shape (same length as a real Discord token).
    private const string BotToken =
        "TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000";

    /// <summary>
    /// Flipping a single bit in the ciphertext body must cause AES-GCM to reject
    /// the authentication tag and throw, preventing silent decryption of tampered data.
    /// </summary>
    [Fact]
    public void AesGcm_BitFlippedCiphertext_ThrowsAuthEx()
    {
        var sut = new AesGcmBotTokenEncryptor(TestKey);
        var envelope = sut.Encrypt(BotToken);

        // Clone the ciphertext and flip the first bit
        var tamperedCiphertext = (byte[])envelope.Ciphertext.Clone();
        tamperedCiphertext[0] ^= 0x01;
        var tampered = envelope with { Ciphertext = tamperedCiphertext };

        Action act = () => sut.Decrypt(tampered);
        act.Should().Throw<CryptographicException>(
            "AES-GCM must detect and reject ciphertext that has been modified after encryption");
    }

    /// <summary>
    /// Replacing the nonce with a different value (even off by one bit) must cause
    /// AES-GCM to fail authentication. This is a strictly different attack vector
    /// than ciphertext tampering: it verifies the nonce is genuinely an input to
    /// the GMAC computation, not merely an IV reused as a derivation seed. A
    /// regression that ever stripped the nonce from the auth-binding would slip
    /// past a ciphertext-only tamper check but be caught here.
    /// </summary>
    [Fact]
    public void AesGcm_TamperedNonce_ThrowsAuthEx()
    {
        var sut = new AesGcmBotTokenEncryptor(TestKey);
        var envelope = sut.Encrypt(BotToken);

        // Clone the nonce and flip the first byte
        var tamperedNonce = (byte[])envelope.Nonce.Clone();
        tamperedNonce[0] ^= 0x01;
        var tampered = envelope with { Nonce = tamperedNonce };

        Action act = () => sut.Decrypt(tampered);
        act.Should().Throw<CryptographicException>(
            "AES-GCM must detect and reject decryption attempts with a nonce that " +
            "does not match the one used at encryption time");
    }
}
