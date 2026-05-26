using Dapper;
using DwbHub.Application.Background;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

// DWBHUB-NO-TENANT-FILTER: login_attempt_log is already allowlisted (rate-limit table).
namespace DwbHub.Infrastructure.Background;

/// <summary>
/// Hangfire background job that hard-deletes <c>login_attempt_log</c> rows older than
/// 90 days. The table is system-wide (no tenant scoping) and is explicitly allowlisted
/// by the tenant-filter lint rule.
/// </summary>
public sealed class LoginAttemptPruneJob(
    IDbConnectionFactory connectionFactory,
    ILogger<LoginAttemptPruneJob> logger) : ILoginAttemptPruneJob
{
    /// <summary>
    /// Delete <c>login_attempt_log</c> rows older than 90 days and log the deletion count.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM login_attempt_log WHERE attempted_at < now() - INTERVAL '90 days'",
            cancellationToken: ct)).ConfigureAwait(false);
        logger.LogInformation("[LoginAttemptPrune] Removed {Count} rows older than 90 days", n);
    }
}
