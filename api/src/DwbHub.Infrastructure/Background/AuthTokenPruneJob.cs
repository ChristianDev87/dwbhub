using Dapper;
using DwbHub.Application.Background;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Background;

/// <summary>
/// Hangfire background job that hard-deletes consumed or long-expired rows from the
/// <c>auth_tokens</c> table. Keeps the table lean; does not affect active tokens.
/// Deletes rows where <c>consumed_at IS NOT NULL</c> or <c>expires_at &lt; now() - 7 days</c>.
/// </summary>
public sealed class AuthTokenPruneJob(
    IDbConnectionFactory connectionFactory,
    ILogger<AuthTokenPruneJob> logger) : IAuthTokenPruneJob
{
    /// <summary>
    /// Delete consumed and long-expired <c>auth_tokens</c> rows and log the deletion count.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            DELETE FROM auth_tokens
            WHERE consumed_at IS NOT NULL
               OR expires_at < now() - INTERVAL '7 days'
            """;
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct))
            .ConfigureAwait(false);
        logger.LogInformation("[AuthTokenPrune] Removed {Count} consumed/expired tokens", n);
    }
}
