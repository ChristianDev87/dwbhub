using System.Net;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Application.Auth;

public sealed class RefreshTokenService(
    IRefreshTokenRepository refreshTokens,
    IUserRepository users,
    ITokenHasher hasher,
    ITokenGenerator generator,
    IJwtIssuer issuer,
    ITenantRepository tenants) : IRefreshTokenService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public async Task<string> IssueForLoginAsync(User user, Tenant tenant, IPAddress? ip, string? userAgent, CancellationToken ct = default)
    {
        var plaintext = generator.GenerateUrlSafeBase64();
        var hash = hasher.Hash(plaintext);
        var expiresAt = DateTimeOffset.UtcNow + Lifetime;
        _ = await refreshTokens.InsertAsync(
            tenant.Id, user.Id, hash, expiresAt,
            user.Role, user.IsActive,
            ip, userAgent, ct).ConfigureAwait(false);
        return plaintext;
    }

    public async Task<RefreshOutcome> RefreshAsync(string refreshTokenPlaintext, IPAddress? ip, string? userAgent, CancellationToken ct = default)
    {
        var oldHash = hasher.Hash(refreshTokenPlaintext);
        var newPlaintext = generator.GenerateUrlSafeBase64();
        var newHash = hasher.Hash(newPlaintext);
        var newExpiresAt = DateTimeOffset.UtcNow + Lifetime;

        var result = await refreshTokens.RotateAsync(oldHash, newHash, newExpiresAt, ip, userAgent, ct).ConfigureAwait(false);

        // Diagnostic-flag decision tree mirrors spec §2.3 last paragraph.
        if (!result.TokenFound)
        {
            return new RefreshOutcome.Invalid();
        }
        if (result.WasRevoked)
        {
            // Replay of an already-revoked token: treat as theft, revoke whole forward chain.
            if (result.OldTokenId is long startId)
            {
                await refreshTokens.RevokeChainAsync(startId, ct).ConfigureAwait(false);
            }
            return new RefreshOutcome.ChainCompromised();
        }
        if (result.NotExpired == false)
        {
            return new RefreshOutcome.Invalid();
        }
        if (result.RightsUnchanged == false)
        {
            // Role/is_active changed since issue — revoke just this token, force re-login.
            _ = await refreshTokens.RevokeByHashAsync(oldHash, ct).ConfigureAwait(false);
            return new RefreshOutcome.RightsChanged();
        }
        if (result.NewTokenId is null || result.UserId is null || result.TenantId is null || result.UserRole is null)
        {
            // Defensive: rotation didn't run despite all flags green. Treat as Invalid.
            return new RefreshOutcome.Invalid();
        }

        // Success: mint a fresh access JWT for the (now-confirmed) current user state.
        var user = await users.GetByIdAsync(result.TenantId.Value, result.UserId.Value, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("User vanished between rotation and JWT issue.");
        var tenant = await tenants.GetByIdAsync(result.TenantId.Value, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tenant vanished between rotation and JWT issue.");
        var accessToken = issuer.Issue(user, tenant);
        return new RefreshOutcome.Success(accessToken, newPlaintext);
    }

    public async Task LogoutAsync(string refreshTokenPlaintext, CancellationToken ct = default)
    {
        var hash = hasher.Hash(refreshTokenPlaintext);
        _ = await refreshTokens.RevokeByHashAsync(hash, ct).ConfigureAwait(false);
    }
}
