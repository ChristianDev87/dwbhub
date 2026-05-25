using System.Net;
using Dapper;
using DwbHub.Api.Hubs;
using DwbHub.Application.Messaging;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Hubs;

/// <summary>
/// Integration tests for MessagesHub using a real ASP.NET Core TestServer.
///
/// Transport: LongPolling (WebSocket support in TestServer requires additional
/// IIS/Kestrel plumbing that is not available in-process; LongPolling exercises
/// the same authentication and authorization code paths).
///
/// Security claims verified:
///   1. A connection without a JWT is rejected (401-equivalent — hub upgrade fails).
///   2. A connection with a valid JWT is accepted and receives only its tenant's
///      messages (tenant isolation).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MessagesHubIntegrationTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;

    public MessagesHubIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var connFactory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(connFactory);
        _users = new UserRepository(connFactory);
        _issuer = new JwtIssuer(Base64JwtKey);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var uniqueLogDir = Path.Combine(
            Path.GetTempPath(), "dwbhub-test-logs", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", uniqueLogDir);
        Environment.SetEnvironmentVariable("DWBHUB_DB_CONNECTION", _fixture.ConnectionString);
        Environment.SetEnvironmentVariable("DWBHUB_JWT_SECRET", Base64JwtKey);
        Environment.SetEnvironmentVariable("DWBHUB_ENCRYPTION_KEY", Base64EncKey);
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_PORT", "11025");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_FROM", "noreply@test.local");
        Environment.SetEnvironmentVariable("DWBHUB_PUBLIC_BASE_URL", "http://localhost:5173");
        Environment.SetEnvironmentVariable("DWBHUB_BOOTSTRAP_TOKEN_FILE", Path.GetTempFileName());
        _factory = new WebApplicationFactory<Program>();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        _ds.Dispose();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<(long tenantId, long userId)> SeedTenantAndUserAsync(
        string slug, string email, UserRole role = UserRole.Owner)
    {
        var tenantId = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var userId = await _users.CreateAsync(new User(
            Id: 0, TenantId: tenantId, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: "x",
            DisplayName: "Hub Test User",
            Role: role, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        return (tenantId, userId);
    }

    private string IssueJwt(long userId, long tenantId, string slug)
    {
        var user = new User(
            Id: userId, TenantId: tenantId, Email: "hub@test.local",
            EmailVerifiedAt: DateTimeOffset.UtcNow, PasswordHash: "",
            DisplayName: "HubUser", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default);
        var tenant = new Tenant(
            Id: tenantId, Name: $"T-{slug}", Slug: slug, Locale: "en",
            CreatedAt: default, UpdatedAt: default);
        return _issuer.Issue(user, tenant);
    }

    private HubConnection BuildConnection(string? jwt)
    {
        var handler = _factory.Server.CreateHandler();
        var builder = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "api/hubs/messages"),
                opts =>
                {
                    opts.HttpMessageHandlerFactory = _ => handler;
                    opts.Transports = HttpTransportType.LongPolling;
                    if (jwt is not null)
                        opts.AccessTokenProvider = () => Task.FromResult<string?>(jwt);
                });
        return builder.Build();
    }

    // ── Test 1: unauthenticated connect is rejected ──────────────────────────

    [Fact]
    public async Task Connect_WithoutJwt_ConnectionIsRejected()
    {
        var conn = BuildConnection(jwt: null);

        // Expect an exception because [Authorize] on the hub rejects the
        // unauthenticated negotiation/connect request.
        var act = async () => await conn.StartAsync();
        await act.Should().ThrowAsync<Exception>(
            "an unauthenticated connection must be rejected by the [Authorize] hub attribute");

        await conn.DisposeAsync();
    }

    // ── Test 2: tenant A receives its messages; tenant B messages are NOT received ─

    [Fact]
    public async Task Connect_WithJwtForTenantA_ReceivesOnlyTenantABroadcasts()
    {
        var (tidA, uidA) = await SeedTenantAndUserAsync("hub-tenant-a", "a@hub.test");
        var (tidB, _) = await SeedTenantAndUserAsync("hub-tenant-b", "b@hub.test");

        var jwtA = IssueJwt(uidA, tidA, "hub-tenant-a");
        var connA = BuildConnection(jwtA);

        var receivedMessages = new List<object?>();
        connA.On<object>("MessageReceived", msg => receivedMessages.Add(msg));

        await connA.StartAsync();

        // Broadcast to tenant A's group via the server-side IHubContext
        var broadcaster = _factory.Services.GetRequiredService<IMessagesBroadcaster>();
        var msgA = new MessageBroadcastDto(
            Id: 1, TenantId: tidA,
            ChannelPublicId: Guid.NewGuid(),
            AuthorName: "alice", Content: "hello-A",
            SentAt: DateTimeOffset.UtcNow, ViaDwbhub: false);

        await broadcaster.MessageReceivedAsync(msgA, msgA.ChannelPublicId);

        // Brief wait so the LongPolling pump can deliver the message
        await Task.Delay(500);

        receivedMessages.Should().HaveCount(1,
            "tenant A client should receive exactly one message broadcast to tenant A's group");

        // Broadcast to tenant B — tenant A client must NOT receive this
        var msgB = new MessageBroadcastDto(
            Id: 2, TenantId: tidB,
            ChannelPublicId: Guid.NewGuid(),
            AuthorName: "bob", Content: "hello-B",
            SentAt: DateTimeOffset.UtcNow, ViaDwbhub: false);

        await broadcaster.MessageReceivedAsync(msgB, msgB.ChannelPublicId);
        await Task.Delay(500);

        receivedMessages.Should().HaveCount(1,
            "tenant A client must NOT receive a broadcast sent to tenant B's group");

        await connA.StopAsync();
        await connA.DisposeAsync();
    }
}
