using System.Net;
using DwbHub.Application.Auth;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DwbHub.Application.Setup;

public sealed class SetupService(
    ISystemBootstrapLockRepository locks,
    ITenantRepository tenants,
    IUserRepository users,
    ITokenHasher tokenHasher,
    IPasswordHasher passwordHasher,
    IEmailVerificationService emailVerification,
    IBootstrapTokenWriter tokenWriter,
    ILogger<SetupService> logger) : ISetupService
{
    public async Task<SetupStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var row = await locks.LoadAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return new SetupStatus(Completed: false, CompletedAt: null);
        }
        return new SetupStatus(
            Completed: row.ConsumedAt.HasValue,
            CompletedAt: row.ConsumedAt);
    }

    public async Task<SetupOutcome> CompleteAsync(
        SetupRequest request,
        IPAddress? ip,
        string? userAgent,
        CancellationToken ct = default)
    {
        // ---- 1. Input validation (cheap, no DB) ----
        if (request.TenantLocale is not "de" and not "en")
        {
            return new SetupOutcome.InvalidRequest("tenantLocale must be 'de' or 'en'.");
        }
        if (!SlugValidator.IsValid(request.TenantSlug))
        {
            return new SetupOutcome.InvalidRequest("tenantSlug does not match ^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$.");
        }
        if (string.IsNullOrWhiteSpace(request.TenantName))
        {
            return new SetupOutcome.InvalidRequest("tenantName is required.");
        }
        if (string.IsNullOrWhiteSpace(request.OwnerEmail))
        {
            return new SetupOutcome.InvalidRequest("ownerEmail is required.");
        }
        if (string.IsNullOrWhiteSpace(request.OwnerDisplayName))
        {
            return new SetupOutcome.InvalidRequest("ownerDisplayName is required.");
        }
        if (PasswordStrength.Validate(request.OwnerPassword) != PasswordStrengthResult.Strong)
        {
            return new SetupOutcome.WeakPassword();
        }

        // ---- 2. Bootstrap-token validation (1 DB hit) ----
        var tokenHashBytes = tokenHasher.Hash(request.BootstrapToken);
        var lockRow = await locks.LoadAsync(ct).ConfigureAwait(false);
        if (lockRow is null || !lockRow.TokenHash.AsSpan().SequenceEqual(tokenHashBytes))
        {
            return new SetupOutcome.InvalidToken();
        }
        if (lockRow.ConsumedAt is not null)
        {
            return new SetupOutcome.AlreadyCompleted();
        }

        // ---- 3. Create tenant (1 DB hit) — guarded against duplicate slug ----
        long tenantId;
        try
        {
            tenantId = await tenants.CreateAsync(
                request.TenantName, request.TenantSlug, request.TenantLocale, ct)
                .ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new SetupOutcome.SlugInUse();
        }

        var tenant = await tenants.GetByIdAsync(tenantId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tenant vanished after insert.");

        // ---- 4. Create owner user (1 DB hit) — unverified, role Owner, active ----
        var ownerUserId = await users.CreateAsync(new User(
            Id: 0,
            TenantId: tenantId,
            Email: request.OwnerEmail,
            EmailVerifiedAt: null,
            PasswordHash: passwordHasher.Hash(request.OwnerPassword),
            DisplayName: request.OwnerDisplayName,
            Role: UserRole.Owner,
            IsActive: true,
            CreatedAt: default,
            UpdatedAt: default), ct).ConfigureAwait(false);

        // ---- 5. Trigger verification email (1 DB hit + SMTP) ----
        var verificationEmailSent = true;
        try
        {
            await emailVerification.ResendAsync(
                tenant.Slug, request.OwnerEmail, request.TenantLocale, ip, userAgent, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            verificationEmailSent = false;
            logger.LogWarning(ex,
                "[Setup] Verification email failed during wizard. Operator can re-trigger via /api/auth/verify-email/resend.");
        }

        // ---- 6. Atomically consume the lock (1 DB hit, race-safe) ----
        var consumed = await locks.TryConsumeAsync(tokenHashBytes, ownerUserId, ct).ConfigureAwait(false);
        if (!consumed)
        {
            logger.LogWarning(
                "[Setup] Lock consume failed unexpectedly after tenant {TenantId} + user {UserId} were created. Concurrent submission?",
                tenantId, ownerUserId);
            return new SetupOutcome.AlreadyCompleted();
        }

        // ---- 7. Best-effort token-file cleanup ----
        var deleted = await tokenWriter.DeleteAsync(ct).ConfigureAwait(false);
        if (!deleted)
        {
            logger.LogWarning(
                "[Setup] Could not delete bootstrap-token file at {Location}. Lock row is consumed; file deletion is hygiene only.",
                tokenWriter.Location);
        }

        return new SetupOutcome.Success(
            TenantId: tenantId,
            TenantSlug: tenant.Slug,
            OwnerUserId: ownerUserId,
            VerificationEmailSent: verificationEmailSent);
    }
}
