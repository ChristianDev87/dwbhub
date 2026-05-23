using DwbHub.Data.Migrations;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DwbHub.Tests.Integration.Infrastructure;

/// <summary>
/// One Postgres container per test session shared across all xUnit collection fixtures.
/// Schema is migrated once on first InitializeAsync.
/// Tests call ResetAsync in their constructor for a clean DB.
/// </summary>
/// <remarks>
/// Plan 0.8.1: xUnit v2 creates a separate PostgresFixture instance per collection
/// (DatabaseCollection + EmailCollection). Both instances share one static container.
///
/// Key constraint: Testcontainers v4 DockerContainer.DisposeAsync() disposes internal
/// synchronisation primitives (SemaphoreSlim). If the first collection disposes the
/// shared container, the second collection's StartAsync call throws
/// ObjectDisposedException. To avoid this, DisposeAsync is a no-op — the container is
/// alive for the lifetime of the test process and is cleaned up by the backend.sh
/// post-run docker rm (or by process exit / --rm on the outer compose service).
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Plan 0.8.1: explicit host port binding (15432 → 5432) so that
    // docker-in-docker runners on custom bridge networks (test-net) can reach this
    // container via the docker0 gateway (172.17.0.1:15432).
    // Docker Desktop for Windows only routes explicitly-mapped ports through the
    // bridge iptables rules; random ephemeral ports are not reachable cross-bridge.
    private static readonly PostgreSqlContainer _sharedContainer = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("dwbhub_test")
        .WithUsername("dwbhub")
        .WithPassword("dwbhub_test_pw")
        .WithPortBinding(15432, 5432)
        .Build();

    private static bool _started;
    private static bool _migrated;
    private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    public string ConnectionString => _sharedContainer.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                await _sharedContainer.StartAsync().ConfigureAwait(false);
                _started = true;
            }

            if (!_migrated)
            {
                var services = new ServiceCollection()
                    .AddFluentMigratorCore()
                    .ConfigureRunner(rb => rb
                        .AddPostgres()
                        .WithGlobalConnectionString(ConnectionString)
                        .ScanIn(typeof(Migration00001_Tenants).Assembly).For.Migrations()
                        .ScanIn(typeof(Migration00001_Tenants).Assembly).For.EmbeddedResources())
                    .AddLogging(lb => lb.AddFluentMigratorConsole())
                    .BuildServiceProvider(false);

                using var scope = services.CreateScope();
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
                _migrated = true;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // No-op: the shared container must not be disposed between collections.
    // It is cleaned up by the backend.sh post-run docker rm step or process exit.
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Truncate every user table (skipping FluentMigrator's VersionInfo)
    /// and restart identity sequences. Called by tests in their ctor.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        const string sql = """
            DO $$
            DECLARE
                tbl TEXT;
            BEGIN
                FOR tbl IN
                    SELECT tablename FROM pg_tables
                    WHERE schemaname = 'public' AND tablename <> 'VersionInfo'
                LOOP
                    EXECUTE format('TRUNCATE TABLE %I RESTART IDENTITY CASCADE', tbl);
                END LOOP;
            END
            $$;
            -- Re-seed singleton rows removed by TRUNCATE.
            INSERT INTO audit_verify_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
