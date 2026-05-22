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
public sealed class AuthTokenPruneJobTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly AuthTokenPruneJob _sut;

    public AuthTokenPruneJobTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        _sut = new AuthTokenPruneJob(new NpgsqlConnectionFactory(_ds),
            NullLogger<AuthTokenPruneJob>.Instance);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Deletes_consumed_and_old_expired_keeps_active()
    {
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();

        // Need a real tenant+user for the FK
        var tid = await conn.QuerySingleAsync<long>(
            "INSERT INTO tenants (name, slug) VALUES ('Test','test') RETURNING id");
        var uid = await conn.QuerySingleAsync<long>("""
            INSERT INTO users (tenant_id, email, password_hash, display_name, role, is_active, email_verified_at)
            VALUES (@T, 'u@test', 'x', 'U', 'Owner', true, now())
            RETURNING id
            """, new { T = tid });

        await conn.ExecuteAsync("""
            INSERT INTO auth_tokens (user_id, tenant_id, email, purpose, token_hash, expires_at, consumed_at) VALUES
              (@U, @T, 'u@test', 'email_verify', '\xAA'::bytea, now() + INTERVAL '1 day', now()),
              (@U, @T, 'u@test', 'email_verify', '\xBB'::bytea, now() - INTERVAL '8 days', NULL),
              (@U, @T, 'u@test', 'email_verify', '\xCC'::bytea, now() + INTERVAL '1 day', NULL)
            """, new { T = tid, U = uid });

        await _sut.RunAsync();

        var remaining = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM auth_tokens");
        remaining.Should().Be(1);
        var hash = await conn.QuerySingleAsync<byte[]>("SELECT token_hash FROM auth_tokens");
        hash.Should().Equal(new byte[] { 0xCC });
    }

    [Fact]
    public async Task Empty_table_is_noop()
    {
        await _sut.RunAsync();
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var count = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM auth_tokens");
        count.Should().Be(0);
    }
}
