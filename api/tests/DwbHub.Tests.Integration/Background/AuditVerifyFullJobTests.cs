using Dapper;
using DwbHub.Application.Audit;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Background;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Background;

[Collection(DatabaseCollection.Name)]
public sealed class AuditVerifyFullJobTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly AuditWriter _writer;
    private readonly AuditVerifyFullJob _sut;

    public AuditVerifyFullJobTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        var repo = new AuditLogRepository(factory);
        _writer = new AuditWriter(repo);
        _sut = new AuditVerifyFullJob(new AuditVerifyCore(repo), _writer,
            NullLogger<AuditVerifyFullJob>.Instance);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Clean_chain_does_not_emit_broken_event_and_leaves_state_alone()
    {
        for (var i = 0; i < 5; i++)
        {
            await _writer.RecordAsync(new AuditEvent(
                null, null, $"seed.{i}", new Dictionary<string, object?> { ["i"] = i }));
        }
        await _sut.RunAsync();

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var brokenCount = await conn.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM audit_log WHERE event_type = 'audit.chain.broken'");
        brokenCount.Should().Be(0);

        var stateId = await conn.QuerySingleAsync<long>(
            "SELECT last_verified_id FROM audit_verify_state WHERE id = 1");
        stateId.Should().Be(0, "full job must NOT advance the incremental state row");
    }

    [Fact]
    public async Task Tampered_old_row_emits_broken_event()
    {
        for (var i = 0; i < 5; i++)
        {
            await _writer.RecordAsync(new AuditEvent(
                null, null, $"seed.{i}", new Dictionary<string, object?> { ["i"] = i }));
        }
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("UPDATE audit_log SET event_type='tampered' WHERE id = 2");

        await _sut.RunAsync();

        var brokenCount = await conn.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM audit_log WHERE event_type = 'audit.chain.broken'");
        brokenCount.Should().Be(1);
    }
}
