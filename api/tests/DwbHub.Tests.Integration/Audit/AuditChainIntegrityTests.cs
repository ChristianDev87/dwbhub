using System.Security.Cryptography;
using System.Text;
using Dapper;
using DwbHub.Application.Audit;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Audit;

[Collection(DatabaseCollection.Name)]
public sealed class AuditChainIntegrityTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly AuditLogRepository _repo;
    private readonly AuditWriter _writer;

    public AuditChainIntegrityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        _repo = new AuditLogRepository(new NpgsqlConnectionFactory(_ds));
        _writer = new AuditWriter(_repo);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task SeedAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _writer.RecordAsync(new AuditEvent(
                TenantId: null, ActorUserId: null,
                EventType: $"test.seed.{i}",
                Payload: new Dictionary<string, object?> { ["i"] = i }));
        }
    }

    private (byte[]? expected, byte[] actual) ChainCheckAt(long id)
    {
        using var conn = _ds.CreateConnection();
        conn.Open();
        var row = conn.QuerySingle<(long? PrevId, byte[]? StoredPrev, byte[] Current, string EventType,
            string PayloadJson, long? TenantId, long? ActorUserId,
            string? Ip, string? Ua, DateTimeOffset OccurredAt)>(
            """
            SELECT
                LAG(id) OVER (ORDER BY id ASC) AS prev_id,
                prev_hash AS stored_prev,
                current_hash AS current,
                event_type, payload_json::text, tenant_id, actor_user_id,
                host(ip_address) AS ip, user_agent, occurred_at
            FROM audit_log WHERE id = @Id
            """, new { Id = id });

        var payloadHash = CanonicalJsonSerializer.HashEvent(
            row.TenantId, row.ActorUserId, row.EventType, row.PayloadJson,
            row.Ip is null ? null : System.Net.IPAddress.Parse(row.Ip), row.Ua, row.OccurredAt);
        var prevForCompute = row.StoredPrev ?? Array.Empty<byte>();
        var combined = new byte[prevForCompute.Length + payloadHash.Length];
        prevForCompute.CopyTo(combined, 0);
        payloadHash.CopyTo(combined, prevForCompute.Length);
        var expectedCurrent = SHA256.HashData(combined);
        return (expectedCurrent, row.Current);
    }

    [Fact]
    public async Task Clean_chain_of_100_rows_verifies_OK()
    {
        await SeedAsync(100);
        for (long id = 1; id <= 100; id++)
        {
            var (expected, actual) = ChainCheckAt(id);
            actual.Should().Equal(expected, $"row {id} should hash correctly");
        }
    }

    [Fact]
    public async Task Tampered_payload_breaks_verify_at_that_row()
    {
        await SeedAsync(10);
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE audit_log SET payload_json = '{\"tampered\":true}'::jsonb WHERE id = 5");

        var (expected, actual) = ChainCheckAt(5);
        actual.Should().NotEqual(expected, "tampered row 5 must have mismatched hash");
    }

    [Fact]
    public async Task Stream_returns_rows_in_ascending_id_order_skipping_startAfterId()
    {
        await SeedAsync(5);
        var ids = new List<long>();
        await foreach (var row in _repo.StreamAscAsync(startAfterId: 2))
        {
            ids.Add(row.Id);
        }
        ids.Should().Equal(new long[] { 3, 4, 5 });
    }
}
