using System.Net;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Application.Auth;

public sealed class LoginService(
    ITenantRepository tenants,
    IUserRepository users,
    ILoginAttemptRepository attempts,
    IPasswordHasher hasher,
    IJwtIssuer issuer,
    IRefreshTokenService refreshTokenService) : ILoginService
{
    private const int LockoutThreshold = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);

    public async Task<LoginOutcome> LoginAsync(
        string tenantSlug, string email, string password, IPAddress ipAddress, CancellationToken ct = default)
    {
        // 1. Lockout check
        var since = DateTimeOffset.UtcNow - LockoutWindow;
        var failedCount = await attempts.CountFailedSinceAsync(email, ipAddress, since, ct).ConfigureAwait(false);
        if (failedCount >= LockoutThreshold)
        {
            return new LoginOutcome.LockedOut((int)LockoutWindow.TotalSeconds);
        }

        // 2. Tenant lookup
        var tenant = await tenants.GetBySlugAsync(tenantSlug, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            hasher.VerifyDecoy(password);
            await attempts.RecordAsync(email, ipAddress, success: false, ct).ConfigureAwait(false);
            return new LoginOutcome.InvalidCredentials();
        }

        // 3. User lookup
        var user = await users.GetByEmailAsync(tenant.Id, email, ct).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            hasher.VerifyDecoy(password);
            await attempts.RecordAsync(email, ipAddress, success: false, ct).ConfigureAwait(false);
            return new LoginOutcome.InvalidCredentials();
        }

        // 4. BCrypt verify
        if (!hasher.Verify(password, user.PasswordHash))
        {
            await attempts.RecordAsync(email, ipAddress, success: false, ct).ConfigureAwait(false);
            return new LoginOutcome.InvalidCredentials();
        }

        // 5. Success
        await attempts.RecordAsync(email, ipAddress, success: true, ct).ConfigureAwait(false);
        var token = issuer.Issue(user, tenant);
        var refreshToken = await refreshTokenService.IssueForLoginAsync(user, tenant, ipAddress, userAgent: null, ct).ConfigureAwait(false);
        return new LoginOutcome.Success(token, refreshToken, user, tenant);
    }
}
