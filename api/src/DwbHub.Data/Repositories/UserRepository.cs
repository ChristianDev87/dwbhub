using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

/// <summary>
/// Dapper-backed implementation of <see cref="IUserRepository"/>.
/// </summary>
public sealed class UserRepository(IDbConnectionFactory connectionFactory) : IUserRepository
{
    /// <inheritdoc/>
    public async Task<User?> GetByEmailAsync(long tenantId, string email, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, email, email_verified_at, password_hash,
                   display_name, role, is_active, created_at, updated_at
            FROM users
            WHERE tenant_id = @TenantId AND email = @Email;
            """;
        return await conn.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(sql, new { TenantId = tenantId, Email = email }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<User?> GetByIdAsync(long tenantId, long id, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, email, email_verified_at, password_hash,
                   display_name, role, is_active, created_at, updated_at
            FROM users
            WHERE tenant_id = @TenantId AND id = @Id;
            """;
        return await conn.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(sql, new { TenantId = tenantId, Id = id }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<long> CreateAsync(User user, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO users (tenant_id, email, email_verified_at, password_hash,
                               display_name, role, is_active)
            VALUES (@TenantId, @Email, @EmailVerifiedAt, @PasswordHash,
                    @DisplayName, @Role::text, @IsActive)
            RETURNING id;
            """;
        return await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, new
            {
                user.TenantId,
                user.Email,
                user.EmailVerifiedAt,
                user.PasswordHash,
                user.DisplayName,
                Role = user.Role.ToString(),
                user.IsActive,
            }, cancellationToken: ct))
            .ConfigureAwait(false);
    }
}
