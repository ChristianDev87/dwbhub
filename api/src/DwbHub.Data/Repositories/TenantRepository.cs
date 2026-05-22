using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

public sealed class TenantRepository(IDbConnectionFactory connectionFactory) : ITenantRepository
{
    public async Task<Tenant?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, name, slug, created_at, updated_at
            FROM tenants
            WHERE id = @Id;
            """;
        return await conn.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, name, slug, created_at, updated_at
            FROM tenants
            WHERE slug = @Slug::citext;
            """;
        return await conn.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(sql, new { Slug = slug }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<long> CreateAsync(string name, string slug, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO tenants (name, slug)
            VALUES (@Name, @Slug)
            RETURNING id;
            """;
        return await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, new { Name = name, Slug = slug }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, name, slug, created_at, updated_at
            FROM tenants
            ORDER BY created_at ASC, id ASC;
            """;
        var rows = await conn.QueryAsync<Tenant>(
            new CommandDefinition(sql, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
