using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

/// <summary>
/// Dapper-backed implementation of <see cref="ITenantRepository"/>.
/// </summary>
public sealed class TenantRepository(IDbConnectionFactory connectionFactory) : ITenantRepository
{
    /// <inheritdoc/>
    public async Task<Tenant?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, name, slug, locale, created_at, updated_at,
                   message_edit_window_seconds
            FROM tenants
            WHERE id = @Id;
            """;
        return await conn.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, name, slug, locale, created_at, updated_at,
                   message_edit_window_seconds
            FROM tenants
            WHERE slug = @Slug::citext;
            """;
        return await conn.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(sql, new { Slug = slug }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<long> CreateAsync(string name, string slug, string locale = "de", CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO tenants (name, slug, locale)
            VALUES (@Name, @Slug, @Locale)
            RETURNING id;
            """;
        return await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, new { Name = name, Slug = slug, Locale = locale }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, name, slug, locale, created_at, updated_at,
                   message_edit_window_seconds
            FROM tenants
            ORDER BY created_at ASC, id ASC;
            """;
        var rows = await conn.QueryAsync<Tenant>(
            new CommandDefinition(sql, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task UpdateMessageEditWindowAsync(long tenantId, int? windowSeconds, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE tenants
               SET message_edit_window_seconds = @windowSeconds,
                   updated_at = NOW()
             WHERE id = @tenantId;
            """;
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { tenantId, windowSeconds }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
