using System.Net;
using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

public interface IAuthTokenRepository
{
    Task<VerifyResendResult> IssueEmailVerifyTokenAsync(
        long tenantId, string email, byte[] tokenHash, DateTimeOffset expiresAt,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default);

    Task<VerifyConfirmResult> ConfirmEmailVerifyAsync(
        byte[] tokenHash,
        CancellationToken ct = default);

    Task<ResetRequestResult> IssuePasswordResetTokenAsync(
        long tenantId, string email, byte[] tokenHash, DateTimeOffset expiresAt,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default);

    Task<ResetConfirmResult> ConfirmPasswordResetAsync(
        byte[] tokenHash, string newPasswordHash,
        CancellationToken ct = default);
}
