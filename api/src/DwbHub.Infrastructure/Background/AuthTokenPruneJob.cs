using Dapper;
using DwbHub.Application.Background;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Background;

public sealed class AuthTokenPruneJob(
    IDbConnectionFactory connectionFactory,
    ILogger<AuthTokenPruneJob> logger) : IAuthTokenPruneJob
{
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
