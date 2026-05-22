using System.Net;
using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

// DWBHUB-NO-TENANT-FILTER: refresh_tokens.token_hash is a globally-UNIQUE 32-byte
// SHA-256 digest — collision across tenants is cryptographically infeasible, so
// `WHERE token_hash = …` already identifies exactly one row. The rotation CTE and
// InsertAsync both still enforce tenant_id via the users JOIN; revoke-by-hash and
// revoke-chain are id/hash-keyed and don't need an additional tenant_id predicate.
namespace DwbHub.Data.Repositories;

public sealed class RefreshTokenRepository(IDbConnectionFactory connectionFactory) : IRefreshTokenRepository
{
    public async Task<long> InsertAsync(
        long tenantId, long userId, byte[] tokenHash, DateTimeOffset expiresAt,
        UserRole issuedRole, bool issuedWasActive,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO refresh_tokens
                (tenant_id, user_id, token_hash, expires_at,
                 issued_role, issued_was_active, ip_address, user_agent)
            VALUES (@TenantId, @UserId, @TokenHash, @ExpiresAt,
                    @Role::text, @WasActive, @IpAddress::inet, @UserAgent)
            RETURNING id;
            """;
        return await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, new
            {
                TenantId = tenantId,
                UserId = userId,
                TokenHash = tokenHash,
                ExpiresAt = expiresAt,
                Role = issuedRole.ToString(),
                WasActive = issuedWasActive,
                IpAddress = ip?.ToString(),
                UserAgent = userAgent,
            }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<RotationResult> RotateAsync(
        byte[] oldHash, byte[] newHash, DateTimeOffset newExpiresAt,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        // SINGLE-CTE rotation: lookup + validate + insert-new + revoke-old in one
        // round-trip. Verbatim per spec §2.3 — do not abbreviate or reformat.
        const string sql = """
            WITH old_token AS (
                SELECT rt.id AS rt_id, rt.revoked_at, rt.expires_at,
                       rt.issued_role, rt.issued_was_active, rt.replaced_by_token_id,
                       u.role        AS u_role,
                       u.is_active   AS u_is_active,
                       u.id          AS user_id,
                       u.tenant_id
                FROM refresh_tokens rt
                INNER JOIN users u ON u.id = rt.user_id AND u.tenant_id = rt.tenant_id
                WHERE rt.token_hash = @OldHash
            ),
            new_token AS (
                INSERT INTO refresh_tokens
                    (tenant_id, user_id, token_hash, expires_at,
                     issued_role, issued_was_active, ip_address, user_agent)
                SELECT tenant_id, user_id, @NewHash, @NewExpiresAt,
                       u_role, u_is_active, @IpAddress::inet, @UserAgent
                FROM old_token
                WHERE revoked_at IS NULL
                  AND expires_at > now()
                  AND issued_role     = u_role
                  AND issued_was_active = u_is_active
                RETURNING id
            ),
            revoke_old AS (
                UPDATE refresh_tokens
                SET revoked_at           = now(),
                    replaced_by_token_id = (SELECT id FROM new_token)
                WHERE id = (SELECT rt_id FROM old_token)
                  AND EXISTS (SELECT 1 FROM new_token)
                RETURNING id
            )
            SELECT
                (SELECT id FROM new_token)                                          AS new_token_id,
                (SELECT rt_id FROM old_token)                                       AS old_token_id,
                (SELECT user_id FROM old_token)                                     AS user_id,
                (SELECT tenant_id FROM old_token)                                   AS tenant_id,
                (SELECT u_role FROM old_token)                                AS user_role,
                ((SELECT rt_id FROM old_token) IS NOT NULL)                         AS token_found,
                ((SELECT revoked_at FROM old_token) IS NOT NULL)                    AS was_revoked,
                (SELECT expires_at > now() FROM old_token)                          AS not_expired,
                (SELECT issued_role = u_role
                    AND issued_was_active = u_is_active FROM old_token)       AS rights_unchanged;
            """;
        return await conn.QuerySingleAsync<RotationResult>(
            new CommandDefinition(sql, new
            {
                OldHash = oldHash,
                NewHash = newHash,
                NewExpiresAt = newExpiresAt,
                IpAddress = ip?.ToString(),
                UserAgent = userAgent,
            }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<bool> RevokeByHashAsync(byte[] tokenHash, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE refresh_tokens
            SET revoked_at = now()
            WHERE token_hash = @TokenHash AND revoked_at IS NULL
            RETURNING id;
            """;
        var id = await conn.ExecuteScalarAsync<long?>(
            new CommandDefinition(sql, new { TokenHash = tokenHash }, cancellationToken: ct))
            .ConfigureAwait(false);
        return id.HasValue;
    }

    public async Task RevokeChainAsync(long startTokenId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            WITH RECURSIVE chain AS (
                SELECT id, replaced_by_token_id
                FROM refresh_tokens
                WHERE id = @StartId
                UNION
                SELECT rt.id, rt.replaced_by_token_id
                FROM refresh_tokens rt
                INNER JOIN chain c ON rt.id = c.replaced_by_token_id
            )
            UPDATE refresh_tokens
            SET revoked_at = COALESCE(revoked_at, now())
            WHERE id IN (SELECT id FROM chain);
            """;
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { StartId = startTokenId }, cancellationToken: ct))
            .ConfigureAwait(false);
    }
}
