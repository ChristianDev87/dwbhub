using System.Net;
using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Audit;

[Collection(DatabaseCollection.Name)]
public sealed class AuditLogRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly AuditLogRepository _sut;

    public AuditLogRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        _sut = new AuditLogRepository(new NpgsqlConnectionFactory(_ds));
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private static byte[] FakeHash(string s) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Insert_first_row_has_null_prev_hash()
    {
        var (_, currentHash) = await _sut.InsertAsync(
            tenantId: null, actorUserId: null, eventType: "system.test",
            payloadJson: "{}", payloadHash: FakeHash("first"),
            occurredAt: DateTimeOffset.UtcNow, ipAddress: null, userAgent: null);

        currentHash.Should().HaveCount(32);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var prev = await conn.QuerySingleAsync<byte[]?>(
            "SELECT prev_hash FROM audit_log ORDER BY id ASC LIMIT 1");
        prev.Should().BeNull();
    }

    [Fact]
    public async Task Insert_second_row_chains_to_first()
    {
        var (_, first) = await _sut.InsertAsync(
            null, null, "system.first", "{}", FakeHash("a"),
            DateTimeOffset.UtcNow, null, null);
        var (_, second) = await _sut.InsertAsync(
            null, null, "system.second", "{}", FakeHash("b"),
            DateTimeOffset.UtcNow, null, null);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var prev = await conn.QuerySingleAsync<byte[]>(
            "SELECT prev_hash FROM audit_log ORDER BY id ASC OFFSET 1 LIMIT 1");
        prev.Should().Equal(first);
        second.Should().NotEqual(first);
    }

    [Fact]
    public async Task Concurrent_inserts_produce_unbroken_chain()
    {
        // Fire 50 inserts in parallel; advisory lock should serialize them.
        var tasks = Enumerable.Range(0, 50).Select(i => _sut.InsertAsync(
            null, null, $"system.parallel.{i}", "{}", FakeHash($"p{i}"),
            DateTimeOffset.UtcNow, null, null)).ToArray();
        await Task.WhenAll(tasks);

        // Walk the chain and verify each row's prev_hash matches the previous row's current_hash.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var rows = (await conn.QueryAsync<(long Id, byte[]? PrevHash, byte[] CurrentHash)>(
            "SELECT id, prev_hash, current_hash FROM audit_log ORDER BY id ASC")).ToList();
        rows.Should().HaveCount(50);
        rows[0].PrevHash.Should().BeNull();
        for (var i = 1; i < rows.Count; i++)
        {
            rows[i].PrevHash.Should().Equal(rows[i - 1].CurrentHash,
                $"row id={rows[i].Id} should chain to row id={rows[i - 1].Id}");
        }
    }

    [Fact]
    public async Task Payload_is_stored_as_queryable_jsonb()
    {
        await _sut.InsertAsync(
            tenantId: null, actorUserId: null, eventType: "system.jsonb",
            payloadJson: @"{""key"":""value""}", payloadHash: FakeHash("j"),
            occurredAt: DateTimeOffset.UtcNow, ipAddress: null, userAgent: null);

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var v = await conn.QuerySingleAsync<string>(
            "SELECT payload_json->>'key' FROM audit_log WHERE event_type='system.jsonb'");
        v.Should().Be("value");
    }

    [Fact]
    public async Task Tenant_id_null_is_allowed_for_system_events()
    {
        var (id, _) = await _sut.InsertAsync(
            tenantId: null, actorUserId: null, eventType: "setup.completed",
            payloadJson: "{}", payloadHash: FakeHash("s"),
            occurredAt: DateTimeOffset.UtcNow, ipAddress: null, userAgent: null);
        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Invalid_actor_user_id_raises_fk_violation()
    {
        var act = async () => await _sut.InsertAsync(
            tenantId: null, actorUserId: 99999, eventType: "system.fk",
            payloadJson: "{}", payloadHash: FakeHash("fk"),
            occurredAt: DateTimeOffset.UtcNow, ipAddress: null, userAgent: null);
        await act.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == "23503");
    }
}
