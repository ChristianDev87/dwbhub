using System.Net;

namespace DwbHub.Application.Auth;

public interface IPasswordResetService
{
    Task RequestAsync(string tenantSlug, string email, string locale, IPAddress? ip, string? userAgent, CancellationToken ct = default);
    Task<ResetConfirmOutcome> ConfirmAsync(string tokenPlaintext, string newPassword, CancellationToken ct = default);
}
