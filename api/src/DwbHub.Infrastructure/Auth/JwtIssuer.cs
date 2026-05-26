using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DwbHub.Application.Auth;
using DwbHub.Core.Entities;
using Microsoft.IdentityModel.Tokens;

namespace DwbHub.Infrastructure.Auth;

/// <summary>
/// Issues HS256 access tokens. Key comes from DWBHUB_JWT_SECRET (Base64-encoded,
/// &#x2265;32 bytes after decode). Lifetime is 24 hours.
/// </summary>
public sealed class JwtIssuer : IJwtIssuer
{
    private readonly byte[] _signingKey;
    private readonly TimeSpan _lifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Initialise the issuer with a Base64-encoded signing key.
    /// </summary>
    /// <param name="base64Secret">Base64-encoded HMAC-SHA256 key; must decode to at least 32 bytes.</param>
    /// <exception cref="InvalidOperationException">Thrown when the decoded key is shorter than 32 bytes.</exception>
    public JwtIssuer(string base64Secret)
    {
        _signingKey = Convert.FromBase64String(base64Secret);
        if (_signingKey.Length < 32)
        {
            throw new InvalidOperationException(
                "DWBHUB_JWT_SECRET must decode to at least 32 bytes (HS256 minimum).");
        }
    }

    /// <inheritdoc/>
    public string Issue(User user, Tenant tenant)
    {
        var now = DateTime.UtcNow;
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: null,
            audience: null,
            subject: new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim("tid", user.TenantId.ToString()),
                new Claim("tslug", tenant.Slug),
                new Claim("role", user.Role.ToString()),
                new Claim(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ]),
            notBefore: now,
            expires: now.Add(_lifetime),
            issuedAt: now,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256));
        return handler.WriteToken(token);
    }
}
