using System.Data;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Hands out open ADO.NET connections. Implemented in DwbHub.Data
/// (NpgsqlConnectionFactory) so Core stays free of Npgsql.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>Open and return a new database connection. The caller is responsible for disposing it.</summary>
    Task<IDbConnection> OpenAsync(CancellationToken ct = default);
}
