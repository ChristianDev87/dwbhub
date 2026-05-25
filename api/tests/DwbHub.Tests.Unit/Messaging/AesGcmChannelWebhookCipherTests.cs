using DwbHub.Application.Messaging;
using DwbHub.Infrastructure.Messaging;
using FluentAssertions;
using System.Security.Cryptography;

namespace DwbHub.Tests.Unit.Messaging;

public sealed class AesGcmChannelWebhookCipherTests
{
    // 32 zero bytes, Base64-encoded — only for tests.
    private const string TestKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public void Roundtrip_RecoversOriginalToken()
    {
        var cipher = new AesGcmChannelWebhookCipher(TestKey);
        const string token = "abc123_thisIsATestWebhookToken-with-symbols.";

        var envelope = cipher.Encrypt(token);
        cipher.Decrypt(envelope).Should().Be(token);
    }

    [Fact]
    public void Encrypt_GeneratesUniqueNonces_AcrossCalls()
    {
        var cipher = new AesGcmChannelWebhookCipher(TestKey);
        var a = cipher.Encrypt("same-token");
        var b = cipher.Encrypt("same-token");

        a.Nonce.Should().NotEqual(b.Nonce);
        a.Ciphertext.Should().NotEqual(b.Ciphertext);   // because nonces differ
    }

    [Fact]
    public void Decrypt_RejectsUnknownKeyVersion()
    {
        var cipher = new AesGcmChannelWebhookCipher(TestKey);
        var envelope = cipher.Encrypt("x") with { KeyVersion = 999 };

        Action act = () => cipher.Decrypt(envelope);
        act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported key version 999*");
    }

    [Fact]
    public void Constructor_RejectsNon32ByteKey()
    {
        Action act = () => new AesGcmChannelWebhookCipher(Convert.ToBase64String(new byte[16]));
        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void Constructor_RejectsNullOrEmptyKey()
    {
        // Null, empty, and whitespace must all surface as a clean ArgumentException
        // (NOT FormatException from Base64 decode of empty input).
        Action act1 = () => new AesGcmChannelWebhookCipher(null!);
        Action act2 = () => new AesGcmChannelWebhookCipher("");
        Action act3 = () => new AesGcmChannelWebhookCipher("   ");

        act1.Should().Throw<ArgumentException>().WithMessage("*must not be null, empty, or whitespace*");
        act2.Should().Throw<ArgumentException>().WithMessage("*must not be null, empty, or whitespace*");
        act3.Should().Throw<ArgumentException>().WithMessage("*must not be null, empty, or whitespace*");
    }
}
