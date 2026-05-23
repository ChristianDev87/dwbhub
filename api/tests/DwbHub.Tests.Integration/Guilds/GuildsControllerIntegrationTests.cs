using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Guilds;

[Collection(DatabaseCollection.Name)]
public sealed class GuildsControllerIntegrationTests : IAsyncLifetime
{
    private const string Base64Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public GuildsControllerIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64Key);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var uniqueLogDir = Path.Combine(Path.GetTempPath(), "dwbhub-test-logs", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", uniqueLogDir);
        Environment.SetEnvironmentVariable("DWBHUB_DB_CONNECTION", _fixture.ConnectionString);
        Environment.SetEnvironmentVariable("DWBHUB_JWT_SECRET", Base64Key);
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_PORT", "11025");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_FROM", "noreply@test.local");
        Environment.SetEnvironmentVariable("DWBHUB_PUBLIC_BASE_URL", "http://localhost:5173");
        Environment.SetEnvironmentVariable("DWBHUB_BOOTSTRAP_TOKEN_FILE", Path.GetTempFileName());
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync().ConfigureAwait(false);
        _ds.Dispose();
    }

    private async Task<(long tenantId, long userId, string jwt)> SeedOwnerAsync(string slug, string email)
    {
        var tid = await _tenants.CreateAsync(name: $"Tenant {slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: "Owner", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", "Owner", UserRole.Owner, true, default, default);
        var tenant = new Tenant(tid, $"T{tid}", slug, "de", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    private async Task<(long tenantId, long userId, string jwt)> SeedMemberAsync(string slug, string email)
    {
        var tid = await _tenants.CreateAsync(name: $"Tenant {slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: "Mem", Role: UserRole.Member, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", "Mem", UserRole.Member, true, default, default);
        var tenant = new Tenant(tid, $"T{tid}", slug, "de", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    [Fact]
    public async Task Happy_path_POST_then_GET_then_DELETE()
    {
        var (_, _, jwt) = await SeedOwnerAsync("acme", "owner@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var post = await _client.PostAsJsonAsync(
            "/api/t/acme/guilds",
            new { discordGuildId = "1234567890123456789", displayName = "Production" });
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var posted = await post.Content.ReadFromJsonAsync<GuildShape>();
        posted!.publicId.Should().NotBeEmpty();
        posted.displayName.Should().Be("Production");

        var get = await _client.GetAsync("/api/t/acme/guilds");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await get.Content.ReadFromJsonAsync<GuildListShape>();
        list!.guilds.Should().HaveCount(1);
        list.guilds[0].publicId.Should().Be(posted.publicId);

        var del = await _client.DeleteAsync($"/api/t/acme/guilds/{posted.publicId:D}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getAfter = await _client.GetAsync("/api/t/acme/guilds");
        var listAfter = await getAfter.Content.ReadFromJsonAsync<GuildListShape>();
        listAfter!.guilds.Should().BeEmpty();
    }

    [Fact]
    public async Task POST_as_member_returns_403()
    {
        var (_, _, jwt) = await SeedMemberAsync("acme", "mem@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var post = await _client.PostAsJsonAsync(
            "/api/t/acme/guilds",
            new { discordGuildId = "1234567890123456789", displayName = "X" });
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task POST_with_malformed_discord_id_returns_400()
    {
        var (_, _, jwt) = await SeedOwnerAsync("acme", "owner@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var post = await _client.PostAsJsonAsync(
            "/api/t/acme/guilds",
            new { discordGuildId = "not-numeric", displayName = "X" });
        post.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_duplicate_returns_409()
    {
        var (_, _, jwt) = await SeedOwnerAsync("acme", "owner@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        await _client.PostAsJsonAsync(
            "/api/t/acme/guilds",
            new { discordGuildId = "1234567890123456789", displayName = "Production" });

        var dup = await _client.PostAsJsonAsync(
            "/api/t/acme/guilds",
            new { discordGuildId = "1234567890123456789", displayName = "Different Name" });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DELETE_nonexistent_returns_404()
    {
        var (_, _, jwt) = await SeedOwnerAsync("acme", "owner@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var del = await _client.DeleteAsync($"/api/t/acme/guilds/{Guid.NewGuid():D}");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record GuildShape(Guid publicId, string discordGuildId, string displayName, bool isActive, DateTimeOffset registeredAt, bool botCredentialsConfigured);
    private sealed record GuildListShape(IReadOnlyList<GuildShape> guilds);
}
