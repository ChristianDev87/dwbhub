using System.Security.Cryptography;
using DwbHub.Core.Encryption;
using DwbHub.Infrastructure.Encryption;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Encryption;

public sealed class AesGcmBotTokenEncryptorTests
{
    // 32 zero bytes base64-encoded — never reused in production.
    private const string TestKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public void Encrypt_then_Decrypt_returns_original_plaintext()
    {
        var sut = new AesGcmBotTokenEncryptor(TestKey);
        const string plaintext = "TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000";

        var envelope = sut.Encrypt(plaintext);
        var decrypted = sut.Decrypt(envelope);

        decrypted.Should().Be(plaintext);
        envelope.Nonce.Length.Should().Be(12);
        envelope.Tag.Length.Should().Be(16);
        envelope.Ciphertext.Length.Should().Be(System.Text.Encoding.UTF8.GetByteCount(plaintext));
    }

    [Fact]
    public void Encrypt_called_twice_produces_different_nonce_and_ciphertext()
    {
        var sut = new AesGcmBotTokenEncryptor(TestKey);
        const string plaintext = "TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000";

        var first = sut.Encrypt(plaintext);
        var second = sut.Encrypt(plaintext);

        first.Nonce.Should().NotEqual(second.Nonce);
        first.Ciphertext.Should().NotEqual(second.Ciphertext);
    }

    [Fact]
    public void Decrypt_with_tampered_ciphertext_throws_CryptographicException()
    {
        var sut = new AesGcmBotTokenEncryptor(TestKey);
        var envelope = sut.Encrypt("TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000");

        var tamperedCiphertext = (byte[])envelope.Ciphertext.Clone();
        tamperedCiphertext[0] ^= 0xFF;
        var tampered = envelope with { Ciphertext = tamperedCiphertext };

        Action act = () => sut.Decrypt(tampered);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_with_wrong_key_throws_CryptographicException()
    {
        var producer = new AesGcmBotTokenEncryptor(TestKey);
        var envelope = producer.Encrypt("TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000");

        const string otherKey = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBA=";
        var consumer = new AesGcmBotTokenEncryptor(otherKey);

        Action act = () => consumer.Decrypt(envelope);
        act.Should().Throw<CryptographicException>();
    }
}
