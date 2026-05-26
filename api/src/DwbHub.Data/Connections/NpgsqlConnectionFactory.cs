using System.Data;
using DwbHub.Core.Repositories;
using Npgsql;

namespace DwbHub.Data.Connections;

/// <summary>
/// Opens pooled NpgsqlConnections via a singleton NpgsqlDataSource.
/// Caller is responsible for disposing the returned IDbConnection.
/// </summary>
public sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    /// <inheritdoc/>
    public async Task<IDbConnection> OpenAsync(CancellationToken ct = default)
    {
        return await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
    }
}
