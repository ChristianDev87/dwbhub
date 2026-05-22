using System.Net;

namespace DwbHub.Application.Auth;

public interface IEmailVerificationService
{
    Task ResendAsync(string tenantSlug, string email, string locale, IPAddress? ip, string? userAgent, CancellationToken ct = default);
    Task<VerifyConfirmOutcome> ConfirmAsync(string tokenPlaintext, CancellationToken ct = default);
}
