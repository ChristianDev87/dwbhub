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
public sealed class AuditVerifyIncrementalJobTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly AuditLogRepository _repo;
    private readonly AuditWriter _writer;
    private readonly AuditVerifyStateRepository _stateRepo;
    private readonly AuditVerifyCore _core;
    private readonly AuditVerifyIncrementalJob _sut;

    public AuditVerifyIncrementalJobTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _repo = new AuditLogRepository(factory);
        _writer = new AuditWriter(_repo);
        _stateRepo = new AuditVerifyStateRepository();
        _core = new AuditVerifyCore(_repo);
        _sut = new AuditVerifyIncrementalJob(factory, _stateRepo, _core, _writer,
            NullLogger<AuditVerifyIncrementalJob>.Instance);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task Seed(int n)
    {
        for (var i = 0; i < n; i++)
        {
            await _writer.RecordAsync(new AuditEvent(
                null, null, $"seed.{i}",
                new Dictionary<string, object?> { ["i"] = i }));
        }
    }

    private async Task<(long Id, string Status)> LoadStateAsync()
    {
        await using var c = _ds.CreateConnection();
        await c.OpenAsync();
        return await c.QuerySingleAsync<(long Id, string Status)>(
            "SELECT last_verified_id AS id, last_run_status AS status FROM audit_verify_state WHERE id = 1");
    }

    [Fact]
    public async Task First_run_on_clean_chain_advances_state_to_MAX_id()
    {
        await Seed(5);
        await _sut.RunAsync();
        var state = await LoadStateAsync();
        state.Id.Should().Be(5);
        state.Status.Should().Be("ok");
    }

    [Fact]
    public async Task Second_run_with_no_new_rows_leaves_state_unchanged()
    {
        await Seed(3);
        await _sut.RunAsync();
        var before = await LoadStateAsync();

        await _sut.RunAsync();
        var after = await LoadStateAsync();
        after.Id.Should().Be(before.Id);
    }

    [Fact]
    public async Task New_rows_after_first_run_are_verified_and_state_advances()
    {
        await Seed(2);
        await _sut.RunAsync();
        await Seed(3); // appends rows 3,4,5
        await _sut.RunAsync();
        var state = await LoadStateAsync();
        state.Id.Should().Be(5);
        state.Status.Should().Be("ok");
    }

    [Fact]
    public async Task Tampered_new_row_marks_state_broken_and_emits_audit_event()
    {
        await Seed(2);
        await _sut.RunAsync();
        await Seed(1); // row 3
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE audit_log SET event_type = 'tampered' WHERE id = 3");

        await _sut.RunAsync();
        var state = await LoadStateAsync();
        state.Id.Should().Be(2, "last_verified_id stays at last good row");
        state.Status.Should().Be("broken");

        var brokenCount = await conn.QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM audit_log WHERE event_type = 'audit.chain.broken'");
        brokenCount.Should().Be(1);
    }
}
