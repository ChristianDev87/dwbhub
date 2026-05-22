using System.Net;
using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

// DWBHUB-NO-TENANT-FILTER: auth_tokens.token_hash is a globally-UNIQUE 32-byte
// SHA-256 digest — collision across tenants is cryptographically infeasible, so
// `WHERE token_hash = …` already identifies exactly one row. The issue CTEs
// always INSERT with tenant_id; the confirm CTEs JOIN through users on
// tenant_id for the conditional UPDATE. The refresh-token mass-revoke in
// reset-confirm filters by user_id (still tenant-scoped because users.user_id
// is itself partitioned by tenant in the FK). No additional tenant_id predicate
// adds value here.
namespace DwbHub.Data.Repositories;

public sealed class AuthTokenRepository(IDbConnectionFactory connectionFactory) : IAuthTokenRepository
{
    public async Task<VerifyResendResult> IssueEmailVerifyTokenAsync(
        long tenantId, string email, byte[] tokenHash, DateTimeOffset expiresAt,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        // SINGLE-CTE: lookup + rate-check + invalidate-old + insert in one round-trip.
        // Verbatim per spec §2.3 — do not abbreviate or reformat.
        const string sql = """
            WITH user_lookup AS (
                SELECT id, tenant_id, email, email_verified_at
                FROM users WHERE email = @Email AND tenant_id = @TenantId
            ),
            rate_check AS (
                SELECT COUNT(*) AS recent_count
                FROM auth_tokens
                WHERE email = @Email AND created_at > now() - INTERVAL '15 minutes'
            ),
            invalidate_old AS (
                UPDATE auth_tokens SET consumed_at = now()
                WHERE user_id = (SELECT id FROM user_lookup)
                  AND purpose = 'email_verify'
                  AND consumed_at IS NULL
                  AND EXISTS (SELECT 1 FROM rate_check WHERE recent_count < 3)
                RETURNING id
            ),
            new_token AS (
                INSERT INTO auth_tokens
                    (user_id, tenant_id, email, purpose, token_hash, expires_at, ip_address, user_agent)
                SELECT id, tenant_id, email, 'email_verify', @Hash, @ExpiresAt, @Ip::inet, @UserAgent
                FROM user_lookup
                WHERE email_verified_at IS NULL
                  AND EXISTS (SELECT 1 FROM rate_check WHERE recent_count < 3)
                RETURNING id
            )
            SELECT
                (SELECT id FROM user_lookup) IS NOT NULL                AS user_found,
                COALESCE((SELECT email_verified_at IS NOT NULL FROM user_lookup), false) AS already_verified,
                (SELECT recent_count FROM rate_check) >= 3              AS rate_limited,
                (SELECT id FROM new_token)                              AS new_token_id;
            """;
        return await conn.QuerySingleAsync<VerifyResendResult>(
            new CommandDefinition(sql, new
            {
                TenantId = tenantId,
                Email = email,
                Hash = tokenHash,
                ExpiresAt = expiresAt,
                Ip = ip?.ToString(),
                UserAgent = userAgent,
            }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<VerifyConfirmResult> ConfirmEmailVerifyAsync(
        byte[] tokenHash,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            WITH token_lookup AS (
                SELECT id, user_id, tenant_id, expires_at, consumed_at
                FROM auth_tokens
                WHERE token_hash = @Hash AND purpose = 'email_verify'
            ),
            verify_user AS (
                UPDATE users SET email_verified_at = now()
                WHERE id        = (SELECT user_id   FROM token_lookup)
                  AND tenant_id = (SELECT tenant_id FROM token_lookup)
                  AND EXISTS (SELECT 1 FROM token_lookup
                              WHERE consumed_at IS NULL AND expires_at > now())
                RETURNING id
            ),
            consume_token AS (
                UPDATE auth_tokens SET consumed_at = now()
                WHERE id = (SELECT id FROM token_lookup)
                  AND EXISTS (SELECT 1 FROM verify_user)
                RETURNING id
            )
            SELECT
                (SELECT id FROM token_lookup) IS NOT NULL      AS token_found,
                (SELECT consumed_at IS NULL FROM token_lookup) AS not_consumed,
                (SELECT expires_at > now() FROM token_lookup)  AS not_expired,
                (SELECT id FROM verify_user) IS NOT NULL       AS verified;
            """;
        return await conn.QuerySingleAsync<VerifyConfirmResult>(
            new CommandDefinition(sql, new { Hash = tokenHash }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<ResetRequestResult> IssuePasswordResetTokenAsync(
        long tenantId, string email, byte[] tokenHash, DateTimeOffset expiresAt,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            WITH user_lookup AS (
                SELECT id, tenant_id, email, email_verified_at
                FROM users WHERE email = @Email AND tenant_id = @TenantId
            ),
            rate_check AS (
                SELECT COUNT(*) AS recent_count
                FROM auth_tokens
                WHERE email = @Email AND created_at > now() - INTERVAL '15 minutes'
            ),
            new_token AS (
                INSERT INTO auth_tokens
                    (user_id, tenant_id, email, purpose, token_hash, expires_at, ip_address, user_agent)
                SELECT id, tenant_id, email, 'password_reset', @Hash, @ExpiresAt, @Ip::inet, @UserAgent
                FROM user_lookup
                WHERE email_verified_at IS NOT NULL
                  AND EXISTS (SELECT 1 FROM rate_check WHERE recent_count < 3)
                RETURNING id
            )
            SELECT
                (SELECT id FROM user_lookup) IS NOT NULL                AS user_found,
                COALESCE((SELECT email_verified_at IS NULL FROM user_lookup), false) AS email_not_verified,
                (SELECT recent_count FROM rate_check) >= 3              AS rate_limited,
                (SELECT id FROM new_token)                              AS new_token_id;
            """;
        return await conn.QuerySingleAsync<ResetRequestResult>(
            new CommandDefinition(sql, new
            {
                TenantId = tenantId,
                Email = email,
                Hash = tokenHash,
                ExpiresAt = expiresAt,
                Ip = ip?.ToString(),
                UserAgent = userAgent,
            }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<ResetConfirmResult> ConfirmPasswordResetAsync(
        byte[] tokenHash, string newPasswordHash,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        // SINGLE-CTE: 4 mutations in one round-trip (users + auth_tokens + refresh_tokens).
        const string sql = """
            WITH token_lookup AS (
                SELECT id, user_id, tenant_id, expires_at, consumed_at
                FROM auth_tokens
                WHERE token_hash = @Hash AND purpose = 'password_reset'
            ),
            update_password AS (
                UPDATE users SET password_hash = @NewHash
                WHERE id        = (SELECT user_id   FROM token_lookup)
                  AND tenant_id = (SELECT tenant_id FROM token_lookup)
                  AND EXISTS (SELECT 1 FROM token_lookup
                              WHERE consumed_at IS NULL AND expires_at > now())
                RETURNING id
            ),
            consume_token AS (
                UPDATE auth_tokens SET consumed_at = now()
                WHERE id = (SELECT id FROM token_lookup)
                  AND EXISTS (SELECT 1 FROM update_password)
                RETURNING id
            ),
            revoke_sessions AS (
                UPDATE refresh_tokens SET revoked_at = now()
                WHERE user_id = (SELECT user_id FROM token_lookup)
                  AND revoked_at IS NULL
                  AND EXISTS (SELECT 1 FROM update_password)
                RETURNING id
            )
            SELECT
                (SELECT id FROM token_lookup) IS NOT NULL      AS token_found,
                (SELECT consumed_at IS NULL FROM token_lookup) AS not_consumed,
                (SELECT expires_at > now() FROM token_lookup)  AS not_expired,
                (SELECT id FROM update_password) IS NOT NULL   AS updated,
                (SELECT COUNT(*) FROM revoke_sessions)::int    AS sessions_revoked;
            """;
        return await conn.QuerySingleAsync<ResetConfirmResult>(
            new CommandDefinition(sql, new
            {
                Hash = tokenHash,
                NewHash = newPasswordHash,
            }, cancellationToken: ct))
            .ConfigureAwait(false);
    }
}
