using Dapper;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Migrations;

/// <summary>
/// Verifies that migrations 013-016 (Plan 1.0 Task 2) created their tables.
/// PostgresFixture runs all migrations in InitializeAsync — if any migration
/// fails to apply the fixture itself throws and every test in this collection fails.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class Plan10TablesExistTests
{
    private readonly PostgresFixture _fixture;

    public Plan10TablesExistTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("guild_channels")]
    [InlineData("channel_webhooks")]
    [InlineData("messages")]
    [InlineData("channel_backfill_jobs")]
    public async Task Table_Exists(string tableName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var exists = await conn.QuerySingleAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @TableName)",
            new { TableName = tableName });

        exists.Should().BeTrue(because: $"migration for '{tableName}' must have applied cleanly");
    }
}
