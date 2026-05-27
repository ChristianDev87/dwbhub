using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using DwbHub.Application.Bot;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Bot;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private readonly GuildRepository _guilds;
    private readonly GuildBotCredentialRepository _credentials;
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
        _guilds = new GuildRepository(factory);
        _credentials = new GuildBotCredentialRepository(factory);
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
        Environment.SetEnvironmentVariable("DWBHUB_ENCRYPTION_KEY", Base64Key);
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

        var post = await _client.CreateGuildAsync(
            "acme",
            new { discordGuildId = "1234567890123456789", displayName = "Production" });
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var posted = await post.Content.ReadFromJsonAsync<GuildShape>();
        posted!.publicId.Should().NotBeEmpty();
        posted.displayName.Should().Be("Production");

        var get = await _client.ListGuildsAsync("acme");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await get.Content.ReadFromJsonAsync<GuildListShape>();
        list!.guilds.Should().HaveCount(1);
        list.guilds[0].publicId.Should().Be(posted.publicId);

        var del = await _client.DeleteGuildAsync("acme", posted.publicId);
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getAfter = await _client.ListGuildsAsync("acme");
        var listAfter = await getAfter.Content.ReadFromJsonAsync<GuildListShape>();
        listAfter!.guilds.Should().BeEmpty();
    }

    [Fact]
    public async Task POST_as_member_returns_403()
    {
        var (_, _, jwt) = await SeedMemberAsync("acme", "mem@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var post = await _client.CreateGuildAsync(
            "acme",
            new { discordGuildId = "1234567890123456789", displayName = "X" });
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task POST_with_malformed_discord_id_returns_400()
    {
        var (_, _, jwt) = await SeedOwnerAsync("acme", "owner@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var post = await _client.CreateGuildAsync(
            "acme",
            new { discordGuildId = "not-numeric", displayName = "X" });
        post.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_duplicate_returns_409()
    {
        var (_, _, jwt) = await SeedOwnerAsync("acme", "owner@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        await _client.CreateGuildAsync(
            "acme",
            new { discordGuildId = "1234567890123456789", displayName = "Production" });

        var dup = await _client.CreateGuildAsync(
            "acme",
            new { discordGuildId = "1234567890123456789", displayName = "Different Name" });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DELETE_nonexistent_returns_404()
    {
        var (_, _, jwt) = await SeedOwnerAsync("acme", "owner@acme.test");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var del = await _client.DeleteGuildAsync("acme", Guid.NewGuid());
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_IncludesBotConnectionState_FromManager()
    {
        // Arrange: seed a tenant + owner + guild WITH credentials.
        var tid = await _tenants.CreateAsync(name: "BotState Tenant", slug: "botstate");
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: "owner@botstate.test",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("correct horse battery staple"),
            DisplayName: "Owner", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var ownerUser = new User(uid, tid, "owner@botstate.test",
            DateTimeOffset.UtcNow, "", "Owner", UserRole.Owner, true, default, default);
        var tenant = new Tenant(tid, "BotState Tenant", "botstate", "de", default, default);
        var jwt = _issuer.Issue(ownerUser, tenant);

        var (gid, _) = await _guilds.CreateAsync(
            tenantId: tid,
            discordGuildId: "9876543210987654321",
            displayName: "BotStateGuild",
            registeredByUserId: uid);

        // Insert fake credentials so BotCredentialsConfigured = true.
        await _credentials.UpsertAsync(gid, tid, new CipherEnvelope(
            Nonce: Enumerable.Repeat((byte)0x11, 12).ToArray(),
            Ciphertext: Enumerable.Repeat((byte)0x22, 80).ToArray(),
            Tag: Enumerable.Repeat((byte)0x33, 16).ToArray()));

        // Override factory + encryptor so the manager can boot without real Discord.
        // Give this factory its own log directory so the log file doesn't conflict with
        // the class-level factory that InitializeAsync already started.
        var uniqueLogDir = Path.Combine(
            Path.GetTempPath(), "dwbhub-test-logs", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", uniqueLogDir);

        var fakeFactory = new FakeBotConnectionFactory();
        using var scopedFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                svc.RemoveAll<IBotConnectionFactory>();
                svc.AddSingleton<IBotConnectionFactory>(fakeFactory);
                svc.RemoveAll<IBotTokenEncryptor>();
                svc.AddSingleton<IBotTokenEncryptor>(new FakeBotTokenEncryptor());
            }));
        var http = scopedFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        // Wait for BotConnectionManager.StartAsync sweep to connect the guild.
        var conn = await fakeFactory.WaitForConnectAsync(gid, TimeSpan.FromSeconds(5));
        conn.State.Should().Be(BotConnectionState.Connected);

        // Act
        var listResp = await http.ListGuildsAsync("botstate");
        var resp = await listResp.Content.ReadFromJsonAsync<GuildListShape>();

        // Assert
        resp.Should().NotBeNull();
        var row = resp!.guilds.Should().ContainSingle().Subject;
        row.botConnectionState.Should().Be("connected");
    }

    private sealed record GuildShape(Guid publicId, string discordGuildId, string displayName, bool isActive, DateTimeOffset registeredAt, bool botCredentialsConfigured, string? botConnectionState);
    private sealed record GuildListShape(IReadOnlyList<GuildShape> guilds);
}
