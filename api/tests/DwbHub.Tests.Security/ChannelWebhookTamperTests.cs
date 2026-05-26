using DwbHub.Application.Messaging;
using DwbHub.Infrastructure.Messaging;
using FluentAssertions;
using System.Security.Cryptography;

namespace DwbHub.Tests.Security;

/// <summary>
/// Adversarial tests for the channel-webhook cipher (Plan 1.0 Task 4).
/// Each test mutates exactly one envelope component and verifies the
/// authenticator rejects decryption. Extends the Plan 0.8.4 security suite.
/// </summary>
public sealed class ChannelWebhookTamperTests
{
    // 32 zero bytes, Base64-encoded — only for tests.
    private const string TestKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string TestToken = "abc123_realisticWebhookToken-shape.with.symbols";

    [Fact]
    public void Ciphertext_BitFlip_FailsAuthentication()
    {
        var cipher = new AesGcmChannelWebhookCipher(TestKey);
        var envelope = cipher.Encrypt(TestToken);

        var tamperedCiphertext = (byte[])envelope.Ciphertext.Clone();
        tamperedCiphertext[0] ^= 0x01;
        var tampered = envelope with { Ciphertext = tamperedCiphertext };

        Action act = () => cipher.Decrypt(tampered);
        act.Should().Throw<CryptographicException>("AES-GCM must reject modified ciphertext");
    }

    [Fact]
    public void Nonce_BitFlip_FailsAuthentication()
    {
        var cipher = new AesGcmChannelWebhookCipher(TestKey);
        var envelope = cipher.Encrypt(TestToken);

        var tamperedNonce = (byte[])envelope.Nonce.Clone();
        tamperedNonce[0] ^= 0x01;
        var tampered = envelope with { Nonce = tamperedNonce };

        Action act = () => cipher.Decrypt(tampered);
        act.Should().Throw<CryptographicException>("AES-GCM must reject decryption with modified nonce");
    }

    [Fact]
    public void AuthTag_BitFlip_FailsAuthentication()
    {
        var cipher = new AesGcmChannelWebhookCipher(TestKey);
        var envelope = cipher.Encrypt(TestToken);

        var tamperedTag = (byte[])envelope.AuthTag.Clone();
        tamperedTag[^1] ^= 0xFF;
        var tampered = envelope with { AuthTag = tamperedTag };

        Action act = () => cipher.Decrypt(tampered);
        act.Should().Throw<CryptographicException>("modified auth tag must prevent decryption");
    }
}
