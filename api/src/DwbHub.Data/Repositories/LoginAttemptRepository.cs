using System.Net;
using Dapper;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

public sealed class LoginAttemptRepository(IDbConnectionFactory connectionFactory) : ILoginAttemptRepository
{
    public async Task RecordAsync(string email, IPAddress ipAddress, bool success, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO login_attempt_log (email, ip_address, success)
            VALUES (@Email, @IpAddress, @Success);
            """;
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { Email = email, IpAddress = ipAddress, Success = success }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<int> CountFailedSinceAsync(string email, IPAddress ipAddress, DateTimeOffset since, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT COUNT(*)::int
            FROM login_attempt_log
            WHERE email = @Email
              AND ip_address = @IpAddress
              AND success = false
              AND attempted_at > @Since;
            """;
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { Email = email, IpAddress = ipAddress, Since = since }, cancellationToken: ct))
            .ConfigureAwait(false);
    }
}
