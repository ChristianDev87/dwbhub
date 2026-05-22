using DwbHub.Data.Migrations;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DwbHub.Tests.Integration.Infrastructure;

/// <summary>
/// One Postgres container per test session.
/// Schema is migrated once in InitializeAsync.
/// Tests call ResetAsync in their constructor for a clean DB.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("dwbhub_test")
        .WithUsername("dwbhub")
        .WithPassword("dwbhub_test_pw")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

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
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }

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
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
