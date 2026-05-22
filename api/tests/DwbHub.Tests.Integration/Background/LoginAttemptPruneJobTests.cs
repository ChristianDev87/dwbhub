using Dapper;
using DwbHub.Data.Connections;
using DwbHub.Infrastructure.Background;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Background;

[Collection(DatabaseCollection.Name)]
public sealed class LoginAttemptPruneJobTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly LoginAttemptPruneJob _sut;

    public LoginAttemptPruneJobTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        _sut = new LoginAttemptPruneJob(new NpgsqlConnectionFactory(_ds),
            NullLogger<LoginAttemptPruneJob>.Instance);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Deletes_rows_older_than_90_days_keeps_newer()
    {
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO login_attempt_log (email, ip_address, success, attempted_at) VALUES
              ('old@x.test', '127.0.0.1', false, now() - INTERVAL '100 days'),
              ('new@x.test', '127.0.0.1', false, now() - INTERVAL '30 days')
            """);

        await _sut.RunAsync();

        var remaining = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM login_attempt_log");
        remaining.Should().Be(1);
        var email = await conn.QuerySingleAsync<string>("SELECT email FROM login_attempt_log");
        email.Should().Be("new@x.test");
    }

    [Fact]
    public async Task Empty_table_is_noop()
    {
        await _sut.RunAsync();
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var count = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM login_attempt_log");
        count.Should().Be(0);
    }
}
