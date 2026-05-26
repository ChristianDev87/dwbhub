using System.Net;
using DwbHub.Application.Audit;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Application.Auth;

/// <summary>
/// Implements email address verification via token-based confirmation emails.
/// </summary>
public sealed class EmailVerificationService(
    IAuthTokenRepository authTokens,
    ITenantRepository tenants,
    IUserRepository users,
    ITokenHasher hasher,
    ITokenGenerator generator,
    IEmailTemplateRenderer renderer,
    IEmailSender sender,
    string publicBaseUrl,
    IAuditWriter auditWriter) : IEmailVerificationService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public async Task ResendAsync(
        string tenantSlug, string email, string locale, IPAddress? ip, string? userAgent,
        CancellationToken ct = default)
    {
        var tenant = await tenants.GetBySlugAsync(tenantSlug, ct).ConfigureAwait(false);
        if (tenant is null) return;

        var plaintext = generator.GenerateUrlSafeBase64();
        var tokenHash = hasher.Hash(plaintext);
        var expiresAt = DateTimeOffset.UtcNow + Lifetime;

        var result = await authTokens.IssueEmailVerifyTokenAsync(
            tenant.Id, email, tokenHash, expiresAt, ip, userAgent, ct).ConfigureAwait(false);

        if (!result.UserFound || result.AlreadyVerified || result.RateLimited || result.NewTokenId is null)
        {
            return;
        }

        var user = await users.GetByEmailAsync(tenant.Id, email, ct).ConfigureAwait(false);
        if (user is null) return;

        var verifyUrl = $"{publicBaseUrl.TrimEnd('/')}/t/{tenant.Slug}/verify-email?token={Uri.EscapeDataString(plaintext)}";
        var model = new Dictionary<string, string>
        {
            ["tenantName"] = tenant.Name,
            ["tenantSlug"] = tenant.Slug,
            ["userDisplayName"] = user.DisplayName,
            ["verifyUrl"] = verifyUrl,
            ["expiresInHours"] = ((int)Lifetime.TotalHours).ToString(),
        };

        var message = renderer.Render("VerifyEmail", locale, email, model);
        await sender.SendAsync(message, ct).ConfigureAwait(false);

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id, ActorUserId: user.Id, EventType: "auth.verify_email.sent",
            Payload: new Dictionary<string, object?> { ["tenantSlug"] = tenant.Slug },
            IpAddress: ip, UserAgent: userAgent), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<VerifyConfirmOutcome> ConfirmAsync(string tokenPlaintext, CancellationToken ct = default)
    {
        var hash = hasher.Hash(tokenPlaintext);
        var result = await authTokens.ConfirmEmailVerifyAsync(hash, ct).ConfigureAwait(false);

        if (!result.TokenFound || result.NotConsumed != true || result.NotExpired != true || !result.Verified)
        {
            return new VerifyConfirmOutcome.Invalid();
        }

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: null, ActorUserId: null, EventType: "auth.verify_email.confirmed",
            Payload: new Dictionary<string, object?>()),
            ct).ConfigureAwait(false);

        return new VerifyConfirmOutcome.Success();
    }
}
