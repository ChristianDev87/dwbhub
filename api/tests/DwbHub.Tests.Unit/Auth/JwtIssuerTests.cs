using System.IdentityModel.Tokens.Jwt;
using DwbHub.Core.Entities;
using DwbHub.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DwbHub.Tests.Unit.Auth;

public sealed class JwtIssuerTests
{
    // 32 bytes of zeros, Base64-encoded — fine for unit tests.
    private const string TestKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static User SampleUser() => new(
        Id: 42,
        TenantId: 7,
        Email: "a@b.c",
        EmailVerifiedAt: null,
        PasswordHash: "(unused)",
        DisplayName: "Tester",
        Role: UserRole.Admin,
        IsActive: true,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    private static Tenant SampleTenant() => new(
        Id: 7,
        Name: "Demo",
        Slug: "demo",
        Locale: "de",
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Issue_includes_required_claims()
    {
        var sut = new JwtIssuer(TestKey);
        var jwt = sut.Issue(SampleUser(), SampleTenant());

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);

        token.Claims.Should().Contain(c => c.Type == "sub" && c.Value == "42");
        token.Claims.Should().Contain(c => c.Type == "tid" && c.Value == "7");
        token.Claims.Should().Contain(c => c.Type == "tslug" && c.Value == "demo");
        token.Claims.Should().Contain(c => c.Type == "role" && c.Value == "Admin");
        token.Claims.Should().Contain(c => c.Type == "iat");
        token.Claims.Should().Contain(c => c.Type == "exp");
        token.Claims.Should().Contain(c => c.Type == "jti");
    }

    [Fact]
    public void Issue_signs_with_HS256()
    {
        var sut = new JwtIssuer(TestKey);
        var jwt = sut.Issue(SampleUser(), SampleTenant());

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);

        token.SignatureAlgorithm.Should().Be(SecurityAlgorithms.HmacSha256);
    }
}
