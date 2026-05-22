using System.Net;
using DwbHub.Application.Audit;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Application.Auth;

public sealed class LoginService(
    ITenantRepository tenants,
    IUserRepository users,
    ILoginAttemptRepository attempts,
    IPasswordHasher hasher,
    IJwtIssuer issuer,
    IRefreshTokenService refreshTokenService,
    IAuditWriter auditWriter) : ILoginService
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
            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: null, ActorUserId: null, EventType: "auth.login.locked_out",
                Payload: new Dictionary<string, object?> { ["tenantSlug"] = tenantSlug, ["email"] = email },
                IpAddress: ipAddress), ct).ConfigureAwait(false);
            return new LoginOutcome.LockedOut((int)LockoutWindow.TotalSeconds);
        }

        // 2. Tenant lookup
        var tenant = await tenants.GetBySlugAsync(tenantSlug, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            hasher.VerifyDecoy(password);
            await attempts.RecordAsync(email, ipAddress, success: false, ct).ConfigureAwait(false);
            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: null, ActorUserId: null, EventType: "auth.login.failed",
                Payload: new Dictionary<string, object?>
                {
                    ["tenantSlug"] = tenantSlug,
                    ["email"] = email,
                    ["reason"] = "unknown_tenant",
                },
                IpAddress: ipAddress), ct).ConfigureAwait(false);
            return new LoginOutcome.InvalidCredentials();
        }

        // 3. User lookup
        var user = await users.GetByEmailAsync(tenant.Id, email, ct).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            hasher.VerifyDecoy(password);
            await attempts.RecordAsync(email, ipAddress, success: false, ct).ConfigureAwait(false);
            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: tenant.Id, ActorUserId: null, EventType: "auth.login.failed",
                Payload: new Dictionary<string, object?>
                {
                    ["tenantSlug"] = tenantSlug,
                    ["email"] = email,
                    ["reason"] = user is null ? "unknown_user" : "inactive_user",
                },
                IpAddress: ipAddress), ct).ConfigureAwait(false);
            return new LoginOutcome.InvalidCredentials();
        }

        // 4. Email-verify gate
        if (user.EmailVerifiedAt is null)
        {
            await attempts.RecordAsync(email, ipAddress, success: false, ct).ConfigureAwait(false);
            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: tenant.Id, ActorUserId: user.Id, EventType: "auth.login.email_not_verified",
                Payload: new Dictionary<string, object?>
                {
                    ["tenantSlug"] = tenantSlug,
                    ["email"] = email,
                },
                IpAddress: ipAddress), ct).ConfigureAwait(false);
            return new LoginOutcome.EmailNotVerified(user.Email);
        }

        // 5. BCrypt verify
        if (!hasher.Verify(password, user.PasswordHash))
        {
            await attempts.RecordAsync(email, ipAddress, success: false, ct).ConfigureAwait(false);
            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: tenant.Id, ActorUserId: user.Id, EventType: "auth.login.failed",
                Payload: new Dictionary<string, object?>
                {
                    ["tenantSlug"] = tenantSlug,
                    ["email"] = email,
                    ["reason"] = "wrong_password",
                },
                IpAddress: ipAddress), ct).ConfigureAwait(false);
            return new LoginOutcome.InvalidCredentials();
        }

        // 6. Success
        await attempts.RecordAsync(email, ipAddress, success: true, ct).ConfigureAwait(false);
        var token = issuer.Issue(user, tenant);
        var refreshToken = await refreshTokenService.IssueForLoginAsync(user, tenant, ipAddress, userAgent: null, ct).ConfigureAwait(false);
        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id, ActorUserId: user.Id, EventType: "auth.login.success",
            Payload: new Dictionary<string, object?> { ["tenantSlug"] = tenantSlug },
            IpAddress: ipAddress), ct).ConfigureAwait(false);
        return new LoginOutcome.Success(token, refreshToken, user, tenant);
    }
}
