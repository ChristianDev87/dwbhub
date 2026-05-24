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
    /// Flipping a single bit in the authentication tag must cause AES-GCM to reject
    /// the envelope. The tag is the integrity-protection binding nonce + ciphertext,
    /// so any tamper — even to the tag itself — must be caught.
    /// </summary>
    [Fact]
    public void AesGcm_TamperedAuthTag_ThrowsAuthEx()
    {
        var sut = new AesGcmBotTokenEncryptor(TestKey);
        var envelope = sut.Encrypt(BotToken);

        // Clone the tag and flip the last byte
        var tamperedTag = (byte[])envelope.Tag.Clone();
        tamperedTag[^1] ^= 0xFF;
        var tampered = envelope with { Tag = tamperedTag };

        Action act = () => sut.Decrypt(tampered);
        act.Should().Throw<CryptographicException>(
            "a modified authentication tag must prevent decryption of the ciphertext");
    }
}
