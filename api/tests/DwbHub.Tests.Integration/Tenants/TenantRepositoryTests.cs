using Dapper;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;
using System.Data;

namespace DwbHub.Tests.Integration.Tenants;

[Collection(DatabaseCollection.Name)]
public sealed class TenantRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly TenantRepository _sut;
    private readonly NpgsqlDataSource _dataSource;

    public TenantRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        _sut = new TenantRepository(new NpgsqlConnectionFactory(_dataSource));
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync()
    {
        _dataSource.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateAsync_assigns_id_and_persists()
    {
        var id = await _sut.CreateAsync("Acme Corp", "acme");
        id.Should().BeGreaterThan(0);

        var loaded = await _sut.GetByIdAsync(id);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Acme Corp");
        loaded.Slug.Should().Be("acme");
    }

    [Fact]
    public async Task GetBySlugAsync_returns_tenant_when_exists()
    {
        await _sut.CreateAsync("Beta Inc", "beta");
        var found = await _sut.GetBySlugAsync("beta");

        found.Should().NotBeNull();
        found!.Name.Should().Be("Beta Inc");
    }

    [Fact]
    public async Task GetBySlugAsync_returns_null_when_missing()
    {
        var found = await _sut.GetBySlugAsync("does-not-exist");
        found.Should().BeNull();
    }

    [Fact]
    public async Task GetBySlugAsync_is_case_insensitive()
    {
        await _sut.CreateAsync("Gamma Ltd", "gamma");
        var found = await _sut.GetBySlugAsync("GAMMA");

        found.Should().NotBeNull();
        found!.Slug.Should().Be("gamma");
    }

    [Fact]
    public async Task CreateAsync_duplicate_slug_throws_postgres_23505()
    {
        await _sut.CreateAsync("First", "duplicate-slug");

        Func<Task> act = () => _sut.CreateAsync("Second", "duplicate-slug");
        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == "23505");
    }

    [Fact]
    public async Task ListAsync_returns_all_in_creation_order()
    {
        var first = await _sut.CreateAsync("Alpha", "alpha");
        await Task.Delay(20); // ensure distinct created_at timestamps
        var second = await _sut.CreateAsync("Bravo", "bravo");
        await Task.Delay(20);
        var third = await _sut.CreateAsync("Charlie", "charlie");

        var all = await _sut.ListAsync();

        all.Should().HaveCount(3);
        all.Select(t => t.Id).Should().ContainInOrder(first, second, third);
    }
}
