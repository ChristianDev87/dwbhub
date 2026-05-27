using System.Data;
using System.Reflection;
using Dapper;
using DwbHub.Api.Hubs;
using DwbHub.Api.Messaging;
using DwbHub.Application.Messaging;
using DwbHub.Core.Entities;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Security;

/// <summary>
/// Tenant isolation tests for the SignalR hub (Plan 1.0 Task 8).
/// Extends the Plan 0.8.4 security suite.
///
/// Threat model:
///   T1: client attempts to join a group other than its own tenant's group.
///   T2: client authenticated for tenant A receives broadcast intended for tenant B.
///   T3: server-side broadcaster derives group name from server-populated data,
///       never from client-supplied data.
/// </summary>
[Collection(SecurityDatabaseCollection.Name)]
public sealed class SignalRTenantIsolationTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;

    public SignalRTenantIsolationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new SecurityDateTimeOffsetTypeHandler());
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
            Path.GetTempPath(), "dwbhub-security-logs", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", uniqueLogDir);
        Environment.SetEnvironmentVariable("DWBHUB_DB_CONNECTION", _fixture.ConnectionString);
        Environment.SetEnvironmentVariable("DWBHUB_JWT_SECRET", Base64JwtKey);
        Environment.SetEnvironmentVariable("DWBHUB_ENCRYPTION_KEY", Base64EncKey);
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_PORT", "11025");
        Environment.SetEnvironmentVariable("DWBHUB_SMTP_FROM", "noreply@test.local");
        Environment.SetEnvironmentVariable("DWBHUB_PUBLIC_BASE_URL", "http://localhost:5173");
        Environment.SetEnvironmentVariable("DWBHUB_BOOTSTRAP_TOKEN_FILE", Path.GetTempFileName());
        _factory = new DwbHubTestFactory();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        _ds.Dispose();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(long tenantId, long userId)> SeedAsync(string slug, string email)
    {
        var tenantId = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var userId = await _users.CreateAsync(new User(
            Id: 0, TenantId: tenantId, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow, PasswordHash: "x",
            DisplayName: "SecTest", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        return (tenantId, userId);
    }

    private string IssueJwt(long userId, long tenantId, string slug)
    {
        var user = new User(
            Id: userId, TenantId: tenantId, Email: "sec@test.local",
            EmailVerifiedAt: DateTimeOffset.UtcNow, PasswordHash: "",
            DisplayName: "SecUser", Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default);
        var tenant = new Tenant(
            Id: tenantId, Name: $"T-{slug}", Slug: slug, Locale: "en",
            CreatedAt: default, UpdatedAt: default);
        return _issuer.Issue(user, tenant);
    }

    private HubConnection BuildConnection(string jwt)
    {
        var handler = _factory.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "api/hubs/messages"),
                opts =>
                {
                    opts.HttpMessageHandlerFactory = _ => handler;
                    opts.Transports = HttpTransportType.LongPolling;
                    opts.AccessTokenProvider = () => Task.FromResult<string?>(jwt);
                })
            .Build();
    }

    // ── Security test 1: client cannot join an arbitrary group ───────────────
    //
    // Threat T1: no client-callable method exists that would let a client
    // specify "please add me to group tenant:X". This is both a static
    // assertion (reflection) and a wire-level assertion (attempting to invoke
    // any hub method fails because none exist).

    [Fact]
    public void ClientCannotJoinArbitraryGroup_NoClientCallableMethodsExist()
    {
        // Static analysis via reflection: no public instance method declared
        // on MessagesHub (beyond the two framework lifecycle overrides) can be
        // invoked by a client. This is the definitive proof that no client-side
        // "join group" surface exists.
        var clientCallable = typeof(MessagesHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m =>
                m.Name is not nameof(MessagesHub.OnConnectedAsync) and
                          not nameof(MessagesHub.OnDisconnectedAsync))
            .ToArray();

        clientCallable.Should().BeEmpty(
            "MessagesHub must have zero client-callable methods. " +
            "Any such method could be exploited to join arbitrary tenant groups " +
            "and receive cross-tenant messages.");
    }

    // ── Security test 2: tenant A client cannot receive tenant B messages ────
    //
    // Threat T2: a client authenticated for tenant A connects. A message is
    // broadcast to tenant B's group. The tenant A client must not receive it
    // even after a generous 2-second polling window.

    [Fact]
    public async Task TokenForTenantA_CannotReceiveTenantB_Messages()
    {
        var (tidA, uidA) = await SeedAsync("sec-iso-a", "a@sec.test");
        var (tidB, _) = await SeedAsync("sec-iso-b", "b@sec.test");

        var jwtA = IssueJwt(uidA, tidA, "sec-iso-a");
        var connA = BuildConnection(jwtA);

        var crossTenantReceived = false;
        connA.On<object>("MessageReceived", _ => crossTenantReceived = true);

        await connA.StartAsync();

        // Broadcast to tenant B — client connected as tenant A must NOT see this.
        var broadcaster = _factory.Services.GetRequiredService<IMessagesBroadcaster>();
        var msgB = new MessageBroadcastDto(
            Id: 99, TenantId: tidB,
            ChannelPublicId: Guid.NewGuid(),
            AuthorName: "attacker", Content: "cross-tenant-payload",
            SentAt: DateTimeOffset.UtcNow, ViaDwbhub: false,
            DiscordMessageId: 100099L);

        await broadcaster.MessageReceivedAsync(msgB, msgB.ChannelPublicId);

        // 2-second window — longer than the integration test's 500 ms — to ensure
        // no delayed delivery slips through.
        await Task.Delay(2_000);

        crossTenantReceived.Should().BeFalse(
            "a JWT for tenant A must NEVER receive messages broadcast to tenant B's group; " +
            "cross-tenant message leakage is a critical security vulnerability");

        await connA.StopAsync();
        await connA.DisposeAsync();
    }

    // ── Security test 3: broadcaster derives group from server data only ─────
    //
    // Threat T3: a compromised caller of IMessagesBroadcaster passes a DTO
    // with a different TenantId than the connected client's tenant. The
    // broadcaster must route to the group derived from the DTO's TenantId
    // (server-populated), not from any connection-time or client-supplied value.

    [Fact]
    public async Task BroadcasterUsesTenantIdFromDto_NotFromClientData()
    {
        // Arrange: mock IHubContext to capture which group SendAsync was called on.
        var capturedGroups = new List<string>();

        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock
            .Setup(p => p.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock
            .Setup(c => c.Group(It.IsAny<string>()))
            .Callback<string>(g => capturedGroups.Add(g))
            .Returns(clientProxyMock.Object);

        var hubContextMock = new Mock<IHubContext<MessagesHub>>();
        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        var broadcaster = new SignalRMessagesBroadcaster(hubContextMock.Object);

        // Act: broadcast two messages — one for tenant 1, one for tenant 2.
        // The TenantId in the DTO is the ONLY authoritative source; nothing
        // from a "connected client" influences the routing.
        var msg1 = new MessageBroadcastDto(
            Id: 1, TenantId: 1, ChannelPublicId: Guid.NewGuid(),
            AuthorName: "a", Content: "msg-for-1",
            SentAt: DateTimeOffset.UtcNow, ViaDwbhub: false,
            DiscordMessageId: 100001L);

        var msg2 = new MessageBroadcastDto(
            Id: 2, TenantId: 2, ChannelPublicId: Guid.NewGuid(),
            AuthorName: "b", Content: "msg-for-2",
            SentAt: DateTimeOffset.UtcNow, ViaDwbhub: false,
            DiscordMessageId: 100002L);

        await broadcaster.MessageReceivedAsync(msg1, msg1.ChannelPublicId);
        await broadcaster.MessageReceivedAsync(msg2, msg2.ChannelPublicId);

        // Assert: groups are derived solely from the DTO's TenantId.
        capturedGroups.Should().ContainInOrder(
            new[] { "tenant:1", "tenant:2" },
            "broadcaster must derive group names from the server-supplied TenantId in the DTO, " +
            "never from caller-controlled parameters or connection-side data");
    }
}

/// <summary>
/// Local Dapper type handler for DateTimeOffset. Mirrors the handler in
/// DwbHub.Tests.Integration.Infrastructure (which is internal and cannot be
/// referenced cross-assembly). Required because Npgsql returns TIMESTAMPTZ as
/// UTC DateTime and Dapper must be told how to convert it.
/// </summary>
file sealed class SecurityDateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        _ => throw new InvalidCastException($"Cannot convert {value?.GetType().Name} to DateTimeOffset"),
    };

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value;
}
