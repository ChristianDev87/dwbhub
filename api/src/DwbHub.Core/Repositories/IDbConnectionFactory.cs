using System.Data;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Hands out open ADO.NET connections. Implemented in DwbHub.Data
/// (NpgsqlConnectionFactory) so Core stays free of Npgsql.
/// </summary>
public interface IDbConnectionFactory
{
    Task<IDbConnection> OpenAsync(CancellationToken ct = default);
}
