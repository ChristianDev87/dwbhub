using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using DwbHub.Core.Entities;
using DwbHub.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;

namespace DwbHub.Tests.Security;

public sealed class JwtSecurityTests
{
    // 32 zero bytes, Base64-encoded — only for tests.
    private const string TestKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static User SampleUser() => new(
        Id: 1,
        TenantId: 1,
        Email: "sec@test.local",
        EmailVerifiedAt: DateTimeOffset.UtcNow,
        PasswordHash: "(unused)",
        DisplayName: "SecurityTester",
        Role: UserRole.Member,
        IsActive: true,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    private static Tenant SampleTenant() => new(
        Id: 1,
        Name: "SecurityTenant",
        Slug: "sec-tenant",
        Locale: "en",
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Flipping a claim in the payload and keeping the original signature must cause
    /// the token handler to reject the token with a signature-mismatch exception.
    /// </summary>
    [Fact]
    public void Jwt_TamperedPayload_FailsValidation()
    {
        var issuer = new JwtIssuer(TestKey);
        var token = issuer.Issue(SampleUser(), SampleTenant());

        // Split into header.payload.signature
        var parts = token.Split('.');
        parts.Should().HaveCount(3, "a JWS has exactly three dot-separated parts");

        // Decode payload, mutate a claim, re-encode
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        var payloadDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)!;

        // Flip "role" from Member to Owner
        payloadDict["role"] = JsonSerializer.Deserialize<JsonElement>("\"Owner\"");
        var tamperedPayloadJson = JsonSerializer.Serialize(payloadDict);
        var tamperedPayloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(tamperedPayloadJson));

        var tamperedToken = $"{parts[0]}.{tamperedPayloadB64}.{parts[2]}";

        // Validate with the same symmetric key — must reject due to signature mismatch
        var handler = new JwtSecurityTokenHandler();
        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(TestKey));
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        Action act = () => handler.ValidateToken(tamperedToken, validationParams, out _);
        act.Should().Throw<SecurityTokenException>(
            "a token whose payload was modified after signing must be rejected");
    }

    /// <summary>
    /// A hand-crafted token with alg=none and no signature must be rejected.
    /// The validator must never trust unsigned tokens regardless of the claims inside.
    /// </summary>
    [Fact]
    public void Jwt_AlgNoneAttack_IsRejected()
    {
        // Build an unsigned token with escalated claims
        var headerJson = """{"alg":"none","typ":"JWT"}""";
        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub"] = "1",
            ["tid"] = "1",
            ["tslug"] = "sec-tenant",
            ["role"] = "Owner",        // escalated privilege
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["nbf"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = 9_999_999_999L,  // far future
            ["jti"] = Guid.NewGuid().ToString(),
        });

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        // alg=none format: header.payload. (trailing dot, empty signature segment)
        var algNoneToken = $"{headerB64}.{payloadB64}.";

        var handler = new JwtSecurityTokenHandler();
        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(TestKey));
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        Action act = () => handler.ValidateToken(algNoneToken, validationParams, out _);
        act.Should().Throw<SecurityTokenException>(
            "unsigned tokens with alg=none must never be accepted by a correctly configured validator");
    }

    // ---- helpers -------------------------------------------------------

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
