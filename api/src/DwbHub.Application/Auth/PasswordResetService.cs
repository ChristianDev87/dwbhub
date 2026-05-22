using System.Net;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Application.Auth;

public sealed class PasswordResetService(
    IAuthTokenRepository authTokens,
    ITenantRepository tenants,
    IUserRepository users,
    ITokenHasher tokenHasher,
    ITokenGenerator generator,
    IPasswordHasher passwordHasher,
    IEmailTemplateRenderer renderer,
    IEmailSender sender,
    string publicBaseUrl) : IPasswordResetService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public async Task RequestAsync(
        string tenantSlug, string email, string locale, IPAddress? ip, string? userAgent,
        CancellationToken ct = default)
    {
        var tenant = await tenants.GetBySlugAsync(tenantSlug, ct).ConfigureAwait(false);
        if (tenant is null) return;

        var plaintext = generator.GenerateUrlSafeBase64();
        var tokenHash = tokenHasher.Hash(plaintext);
        var expiresAt = DateTimeOffset.UtcNow + Lifetime;

        var result = await authTokens.IssuePasswordResetTokenAsync(
            tenant.Id, email, tokenHash, expiresAt, ip, userAgent, ct).ConfigureAwait(false);

        if (!result.UserFound || result.EmailNotVerified || result.RateLimited || result.NewTokenId is null)
        {
            return;
        }

        var user = await users.GetByEmailAsync(tenant.Id, email, ct).ConfigureAwait(false);
        if (user is null) return;

        var resetUrl = $"{publicBaseUrl.TrimEnd('/')}/t/{tenant.Slug}/password-reset?token={Uri.EscapeDataString(plaintext)}";
        var model = new Dictionary<string, string>
        {
            ["tenantName"] = tenant.Name,
            ["tenantSlug"] = tenant.Slug,
            ["userDisplayName"] = user.DisplayName,
            ["resetUrl"] = resetUrl,
            ["expiresInMinutes"] = ((int)Lifetime.TotalMinutes).ToString(),
        };

        var message = renderer.Render("PasswordReset", locale, email, model);
        await sender.SendAsync(message, ct).ConfigureAwait(false);
    }

    public async Task<ResetConfirmOutcome> ConfirmAsync(string tokenPlaintext, string newPassword, CancellationToken ct = default)
    {
        if (PasswordStrength.Validate(newPassword) != PasswordStrengthResult.Strong)
        {
            return new ResetConfirmOutcome.WeakPassword();
        }

        var hash = tokenHasher.Hash(tokenPlaintext);
        var newPasswordHash = passwordHasher.Hash(newPassword);

        var result = await authTokens.ConfirmPasswordResetAsync(hash, newPasswordHash, ct).ConfigureAwait(false);

        if (!result.TokenFound || result.NotConsumed != true || result.NotExpired != true || !result.Updated)
        {
            return new ResetConfirmOutcome.Invalid();
        }

        return new ResetConfirmOutcome.Success(result.SessionsRevoked);
    }
}
