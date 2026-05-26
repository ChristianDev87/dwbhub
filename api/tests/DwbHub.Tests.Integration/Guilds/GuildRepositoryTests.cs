using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Guilds;

[Collection(DatabaseCollection.Name)]
public sealed class GuildRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _sut;
    private readonly BCryptPasswordHasher _hasher = new();

    public GuildRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _sut = new GuildRepository(factory);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<(long tenantId, long userId)> SeedTenantAndOwnerAsync(string slug = "acme")
    {
        var tid = await _tenants.CreateAsync(name: $"Tenant {slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: $"owner@{slug}.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: "Owner", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        return (tid, uid);
    }

    [Fact]
    public async Task Create_then_GetByPublicId_returns_persisted_row()
    {
        var (tid, uid) = await SeedTenantAndOwnerAsync();
        var (id, pid) = await _sut.CreateAsync(
            tenantId: tid,
            discordGuildId: "1234567890123456789",
            displayName: "Production",
            registeredByUserId: uid);

        id.Should().BeGreaterThan(0);
        pid.Should().NotBeEmpty();

        var loaded = await _sut.GetByPublicIdAsync(pid, tid);
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(id);
        loaded.PublicId.Should().Be(pid);
        loaded.TenantId.Should().Be(tid);
        loaded.DiscordGuildId.Should().Be("1234567890123456789");
        loaded.DisplayName.Should().Be("Production");
        loaded.IsActive.Should().BeTrue();
        loaded.RegisteredByUserId.Should().Be(uid);
        loaded.LastConnectedAt.Should().BeNull();
    }

    [Fact]
    public async Task Create_with_duplicate_discord_guild_id_in_same_tenant_throws_GuildAlreadyExistsException()
    {
        // Plan 1.0 Fix C: repository now uses INSERT ... ON CONFLICT DO NOTHING
        // so PostgreSQL no longer emits a 23505 ERROR log line for the violation.
        // The application instead raises GuildAlreadyExistsException explicitly
        // when zero rows were inserted — the API contract (HTTP 409 from the
        // controller) is preserved, but the postgres log stays clean.
        var (tid, uid) = await SeedTenantAndOwnerAsync();
        await _sut.CreateAsync(tid, "1234567890123456789", "First", uid);

        Func<Task> act = () => _sut.CreateAsync(tid, "1234567890123456789", "Second", uid);
        await act.Should().ThrowAsync<GuildAlreadyExistsException>()
            .Where(e => e.TenantId == tid && e.DiscordGuildId == "1234567890123456789");
    }

    [Fact]
    public async Task ListByTenant_returns_only_own_guilds_in_display_name_order()
    {
        var (tidA, uidA) = await SeedTenantAndOwnerAsync("acme");
        var (tidB, uidB) = await SeedTenantAndOwnerAsync("globex");

        await _sut.CreateAsync(tidA, "1111111111111111111", "Zeta", uidA);
        await _sut.CreateAsync(tidA, "2222222222222222222", "Alpha", uidA);
        await _sut.CreateAsync(tidB, "3333333333333333333", "Cross-Tenant", uidB);

        var listA = await _sut.ListByTenantAsync(tidA);
        listA.Should().HaveCount(2);
        listA.Select(g => g.DisplayName).Should().ContainInOrder("Alpha", "Zeta");
        listA.Should().OnlyContain(g => g.TenantId == tidA);
    }

    [Fact]
    public async Task Delete_existing_row_returns_true_and_removes_it()
    {
        var (tid, uid) = await SeedTenantAndOwnerAsync();
        var (_, pid) = await _sut.CreateAsync(tid, "1234567890123456789", "Production", uid);

        var deleted = await _sut.DeleteAsync(pid, tid);
        deleted.Should().BeTrue();

        var loaded = await _sut.GetByPublicIdAsync(pid, tid);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Delete_nonexistent_returns_false_without_throwing()
    {
        var (tid, _) = await SeedTenantAndOwnerAsync();
        var deleted = await _sut.DeleteAsync(Guid.NewGuid(), tid);
        deleted.Should().BeFalse();
    }
}
