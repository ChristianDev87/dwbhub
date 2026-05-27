using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using DwbHub.Application.Messaging;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for MessagesController.
///
/// Because IMessageService and IGuildChannelRepository are not yet wired in
/// Program.cs (that happens in Task 11), every test factory overrides those
/// services via WithWebHostBuilder. Channel resolution uses real Dapper repos
/// backed by the shared Testcontainer; message-send paths use configurable
/// stubs to exercise exception-translation logic without a real Discord connection.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MessagesControllerTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _channels;
    private readonly MessageRepository _msgRepo;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;

    public MessagesControllerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var fac = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(fac);
        _users = new UserRepository(fac);
        _guilds = new GuildRepository(fac);
        _channels = new GuildChannelRepository(fac);
        _msgRepo = new MessageRepository(fac);
        _hasher = new BCryptPasswordHasher();
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
        _factory = new DwbHubTestFactory();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        _ds.Dispose();
    }

    // ── Seed helpers ─────────────────────────────────────────────────────────

    private async Task<(long tenantId, long userId, string jwt)> SeedOwnerAsync(
        string slug, string email = "owner@test.local", string displayName = "OwnerUser")
    {
        var tid = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: displayName,
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", displayName,
            UserRole.Owner, true, default, default);
        var tenant = new Tenant(tid, $"T-{slug}", slug, "en", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    private async Task<(long channelId, Guid channelPublicId)> SeedBridgedChannelAsync(
        long tenantId, long guildId, long discordChannelId = 100000000000000500L)
    {
        var channel = await _channels.UpsertFromSyncAsync(
            tenantId, guildId, discordChannelId, "general", 0, 0);
        await _channels.SetBridgedAsync(tenantId, channel.PublicId, true);
        // Re-fetch to get updated state
        var updated = await _channels.GetByPublicIdAsync(tenantId, channel.PublicId);
        return (updated!.Id, updated.PublicId);
    }

    private async Task<long> SeedGuildAsync(long tenantId, long userId)
    {
        var (gid, _) = await _guilds.CreateAsync(tenantId, "100000000000000111", "Guild", userId);
        return gid;
    }

    private async Task<Message> SeedMessageAsync(long tenantId, long channelId, long snowflake,
        string content = "Hello world", string author = "TestUser", bool isDeleted = false)
    {
        var msg = new Message
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordMessageId = snowflake,
            DiscordAuthorId = 99999L,
            DiscordAuthorName = author,
            ViaDwbhub = false,
            Content = content,
            SentAt = DateTimeOffset.UtcNow,
        };
        var inserted = await _msgRepo.InsertAsync(msg);
        if (isDeleted)
            await _msgRepo.MarkDeletedAsync(tenantId, snowflake);
        return inserted!;
    }

    /// <summary>
    /// Creates a factory with the messaging services injected.
    /// The IMessageService stub is configurable per test.
    /// </summary>
    private HttpClient BuildClient(string jwt, IMessageService? messageServiceOverride = null)
    {
        var connectionFactory = new NpgsqlConnectionFactory(_ds);
        var channelRepo = new GuildChannelRepository(connectionFactory);
        var msgRepo = new MessageRepository(connectionFactory);
        var userRepo = new UserRepository(connectionFactory);

        // Default stub returns a fake success for SendOutboundAsync, delegates
        // ListHistoryAsync to the real repository.
        IMessageService msgSvc = messageServiceOverride
            ?? new DefaultFakeMessageService(msgRepo);

        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                svc.RemoveAll<IGuildChannelRepository>();
                svc.AddScoped<IGuildChannelRepository>(_ => channelRepo);
                svc.RemoveAll<IMessageService>();
                svc.AddScoped<IMessageService>(_ => msgSvc);
                // IUserRepository is already registered in Program.cs as scoped;
                // we keep the real one backed by the live test DB.
            }))
            .CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // ── 1. POST happy path → 201 ─────────────────────────────────────────────

    [Fact]
    public async Task POST_messages_AuthenticatedTenant_201()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-post-201");
        var gid = await SeedGuildAsync(tid, uid);
        var (channelId, channelPublicId) = await SeedBridgedChannelAsync(tid, gid);

        // Inject a stub that returns a fake sent message.
        var fakeMsg = new Message
        {
            Id = 42,
            TenantId = tid,
            ChannelId = channelId,
            DiscordMessageId = 123456789000000001L,
            DiscordAuthorId = 1L,
            DiscordAuthorName = "OwnerUser",
            ViaDwbhub = true,
            DwbhubUserId = uid,
            Content = "Hello",
            SentAt = DateTimeOffset.UtcNow,
        };
        using var client = BuildClient(jwt, new SendSuccessStub(fakeMsg));

        var res = await client.PostMessageAsync("msg-post-201", channelPublicId,
            new { content = "Hello" });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetInt64().Should().Be(42);
        body.GetProperty("discordMessageId").GetInt64().Should().Be(123456789000000001L);
        body.GetProperty("sentAt").GetString().Should().NotBeNullOrEmpty();
    }

    // ── 2. POST without auth → 401 ───────────────────────────────────────────

    [Fact]
    public async Task POST_messages_NoAuth_401()
    {
        // Seed a real tenant so TenantResolverMiddleware resolves the slug successfully.
        // Without this, the middleware would return 404 before authorization can return 401.
        var (tid, uid, _) = await SeedOwnerAsync("msg-noauth");
        var gid = await SeedGuildAsync(tid, uid);
        var (_, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000000199L);

        using var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                svc.RemoveAll<IGuildChannelRepository>();
                svc.AddScoped<IGuildChannelRepository>(_ =>
                    new GuildChannelRepository(new NpgsqlConnectionFactory(_ds)));
                svc.RemoveAll<IMessageService>();
                svc.AddScoped<IMessageService>(_ => new DefaultFakeMessageService(
                    new MessageRepository(new NpgsqlConnectionFactory(_ds))));
            }))
            .CreateClient();

        // No Authorization header — should return 401.
        var res = await client.PostMessageAsync("msg-noauth", channelPublicId,
            new { content = "Hello" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── 3. POST to another tenant's channel → 404 ────────────────────────────

    [Fact]
    public async Task POST_messages_OtherTenantsChannel_404()
    {
        var (tidA, uidA, jwtA) = await SeedOwnerAsync("msg-xta", "ownera@msg.test");
        var (tidB, uidB, _) = await SeedOwnerAsync("msg-xtb", "ownerb@msg.test");

        var gidB = await SeedGuildAsync(tidB, uidB);
        var (_, channelBPublicId) = await SeedBridgedChannelAsync(tidB, gidB, 100000000000000601L);

        // Tenant A's JWT targeting Tenant B's channel.
        using var client = BuildClient(jwtA);

        var res = await client.PostMessageAsync("msg-xta", channelBPublicId,
            new { content = "sneaky" });

        // Must be 404, NOT 403 — no information disclosure.
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 4. POST to unbridged channel → 409 ───────────────────────────────────

    [Fact]
    public async Task POST_messages_UnbridgedChannel_409()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-unbridged");
        var gid = await SeedGuildAsync(tid, uid);

        // Create channel but do NOT set is_bridged = true.
        var channel = await _channels.UpsertFromSyncAsync(
            tid, gid, 100000000000000700L, "no-bridge", 0, 0);

        using var client = BuildClient(jwt);

        var res = await client.PostMessageAsync("msg-unbridged", channel.PublicId,
            new { content = "Hello" });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("channel_not_bridged");
    }

    // ── 5. POST with empty content → 400 ─────────────────────────────────────

    [Fact]
    public async Task POST_messages_EmptyContent_400()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-empty");
        var gid = await SeedGuildAsync(tid, uid);
        var (_, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000000800L);

        using var client = BuildClient(jwt);

        var res = await client.PostMessageAsync("msg-empty", channelPublicId,
            new { content = "" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 6. POST with whitespace-only content → 400 ───────────────────────────

    [Fact]
    public async Task POST_messages_WhitespaceContent_400()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-ws");
        var gid = await SeedGuildAsync(tid, uid);
        var (_, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000000900L);

        using var client = BuildClient(jwt);

        var res = await client.PostMessageAsync("msg-ws", channelPublicId,
            new { content = "   " });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("content_required");
    }

    // ── 7. POST with 2001-char content → 400 ─────────────────────────────────

    [Fact]
    public async Task POST_messages_Over2000Chars_400()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-long");
        var gid = await SeedGuildAsync(tid, uid);
        var (_, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000001000L);

        using var client = BuildClient(jwt);

        var res = await client.PostMessageAsync("msg-long", channelPublicId,
            new { content = new string('x', 2001) });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 8. POST → Discord rate limit → 503 with Retry-After ─────────────────

    [Fact]
    public async Task POST_messages_DiscordRateLimited_503_RetryAfter()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-rl");
        var gid = await SeedGuildAsync(tid, uid);
        var (_, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000001100L);

        using var client = BuildClient(jwt,
            new ThrowOnSendStub(new DiscordRateLimitException(12, "rate limited")));

        var res = await client.PostMessageAsync("msg-rl", channelPublicId,
            new { content = "Hello" });

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        res.Headers.Should().ContainKey("Retry-After");
        res.Headers.GetValues("Retry-After").First().Should().Be("12");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("discord_rate_limited");
    }

    // ── 9. POST → Discord permission denied → 409 ────────────────────────────

    [Fact]
    public async Task POST_messages_DiscordPermissionDenied_409()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-perm");
        var gid = await SeedGuildAsync(tid, uid);
        var (_, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000001200L);

        using var client = BuildClient(jwt,
            new ThrowOnSendStub(new DiscordPermissionException("Missing SEND_MESSAGES")));

        var res = await client.PostMessageAsync("msg-perm", channelPublicId,
            new { content = "Hello" });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("bot_missing_permission");
    }

    // ── 10. GET → returns seeded messages newest-first ────────────────────────

    [Fact]
    public async Task GET_messages_AuthenticatedTenant_ReturnsHistory()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-get-hist");
        var gid = await SeedGuildAsync(tid, uid);
        var (channelId, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000001300L);

        // Seed 3 messages with ascending snowflakes (newest = highest).
        await SeedMessageAsync(tid, channelId, 1000L, "first", "Alice");
        await SeedMessageAsync(tid, channelId, 2000L, "second", "Bob");
        await SeedMessageAsync(tid, channelId, 3000L, "third", "Carol");

        using var client = BuildClient(jwt);

        var res = await client.GetMessagesAsync("msg-get-hist", channelPublicId, limit: 10);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<HistoryShape>();
        body.Should().NotBeNull();
        body!.messages.Should().HaveCount(3);
        // Newest-first order (snowflake 3000 → 2000 → 1000).
        body.messages[0].content.Should().Be("third");
        body.messages[1].content.Should().Be("second");
        body.messages[2].content.Should().Be("first");
        body.nextBefore.Should().BeNull("fewer rows than limit means no more pages");
    }

    // ── 11. GET on another tenant's channel → 404 ────────────────────────────

    [Fact]
    public async Task GET_messages_OtherTenantsChannel_404()
    {
        var (_, uidA, jwtA) = await SeedOwnerAsync("msg-get-xta", "ga@get.test");
        var (tidB, uidB, _) = await SeedOwnerAsync("msg-get-xtb", "gb@get.test");

        var gidB = await SeedGuildAsync(tidB, uidB);
        var (_, channelBPublicId) = await SeedBridgedChannelAsync(tidB, gidB, 100000000000001400L);

        using var client = BuildClient(jwtA);

        var res = await client.GetMessagesAsync("msg-get-xta", channelBPublicId);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 12. GET with limit=500 → clamped to 100, no error ────────────────────

    [Fact]
    public async Task GET_messages_LimitClamp_To_100_OnTooBig()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-clamp-big");
        var gid = await SeedGuildAsync(tid, uid);
        var (channelId, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000001500L);

        // Seed 5 messages.
        for (var i = 1; i <= 5; i++)
            await SeedMessageAsync(tid, channelId, 2000L + i, $"msg-{i}");

        using var client = BuildClient(jwt);

        // limit=500 should be clamped to 100 — no error, returns what's available.
        var res = await client.GetMessagesAsync("msg-clamp-big", channelPublicId, limit: 500);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<HistoryShape>();
        body.Should().NotBeNull();
        // All 5 messages returned (fewer than 100).
        body!.messages.Should().HaveCount(5);
    }

    // ── 13. GET with limit=−1 → clamped to 50 ────────────────────────────────

    [Fact]
    public async Task GET_messages_LimitClamp_To_50_OnInvalid()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-clamp-neg");
        var gid = await SeedGuildAsync(tid, uid);
        var (channelId, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000001600L);

        // Seed only 2 messages — fewer than 50.
        await SeedMessageAsync(tid, channelId, 3001L, "a");
        await SeedMessageAsync(tid, channelId, 3002L, "b");

        using var client = BuildClient(jwt);

        // limit=-1 should be clamped to 50, no error returned.
        var res = await client.GetMessagesAsync("msg-clamp-neg", channelPublicId, limit: -1);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<HistoryShape>();
        body.Should().NotBeNull();
        body!.messages.Should().HaveCount(2);
        body.nextBefore.Should().BeNull();
    }

    // ── 14. GET with pagination cursor ───────────────────────────────────────

    [Fact]
    public async Task GET_messages_PaginationCursor_NextBeforeIsOldestSnowflake()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-page");
        var gid = await SeedGuildAsync(tid, uid);
        var (channelId, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000001700L);

        // Seed 6 messages with snowflakes 1..6 (newest = 6).
        for (var i = 1; i <= 6; i++)
            await SeedMessageAsync(tid, channelId, 5000L + i, $"msg-{i}");

        using var client = BuildClient(jwt);

        // Request first page of 3 — should return snowflakes 5006, 5005, 5004 (newest-first).
        var res = await client.GetMessagesAsync("msg-page", channelPublicId, limit: 3);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<HistoryShape>();
        body.Should().NotBeNull();
        body!.messages.Should().HaveCount(3);
        // NextBefore = oldest snowflake in page = 5004 = rows[^1].DiscordMessageId
        body.nextBefore.Should().Be(5004L,
            "NextBefore must be the oldest snowflake in the returned page for correct cursor pagination");
    }

    // ── 15. GET when a message is deleted — deleted row is absent ─────────────

    [Fact]
    public async Task GET_messages_DeletedRow_NotPresentInHistory()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("msg-deleted");
        var gid = await SeedGuildAsync(tid, uid);
        var (channelId, channelPublicId) = await SeedBridgedChannelAsync(tid, gid, 100000000000001800L);

        await SeedMessageAsync(tid, channelId, 6001L, "visible message");
        await SeedMessageAsync(tid, channelId, 6002L, "deleted message", isDeleted: true);

        using var client = BuildClient(jwt);

        var res = await client.GetMessagesAsync("msg-deleted", channelPublicId);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<HistoryShape>();
        body.Should().NotBeNull();
        // Only 1 message visible; deleted row is filtered by WHERE deleted_at IS NULL.
        body!.messages.Should().HaveCount(1);
        body.messages[0].content.Should().Be("visible message");
    }

    // ── Response shapes (anonymous-record projections for JSON deserialization) ─

    private sealed record HistoryShape(IReadOnlyList<HistoryItemShape> messages, long? nextBefore);
    private sealed record HistoryItemShape(long id, string authorName, string content,
        DateTimeOffset sentAt, DateTimeOffset? editedAt, bool viaDwbhub);
}

// ── Stub IMessageService implementations ─────────────────────────────────────

/// <summary>
/// Default stub: SendOutboundAsync returns a fake persisted message;
/// ListHistoryAsync delegates to the real repository.
/// </summary>
file sealed class DefaultFakeMessageService(IMessageRepository msgRepo) : IMessageService
{
    public Task<DwbHub.Core.Messaging.Message?> PersistInboundAsync(
        MessageReceivedEvent evt, CancellationToken ct = default)
        => Task.FromResult<DwbHub.Core.Messaging.Message?>(null);

    public Task PersistEditAsync(MessageUpdatedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MarkDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<DwbHub.Core.Messaging.Message> SendOutboundAsync(
        long tenantId, long channelId, long userId, string displayName,
        string content, CancellationToken ct = default)
    {
        var fakeResult = new DwbHub.Core.Messaging.Message
        {
            Id = 1,
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordMessageId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DiscordAuthorId = 1L,
            DiscordAuthorName = displayName,
            ViaDwbhub = true,
            DwbhubUserId = userId,
            Content = content,
            SentAt = DateTimeOffset.UtcNow,
        };
        return Task.FromResult(fakeResult);
    }

    public Task<IReadOnlyList<DwbHub.Core.Messaging.Message>> ListHistoryAsync(
        long tenantId, long channelId, long? beforeSnowflake, int limit,
        CancellationToken ct = default)
        => msgRepo.ListByChannelBeforeAsync(tenantId, channelId, beforeSnowflake, limit, ct);
}

/// <summary>
/// Stub that returns a specific pre-built message from SendOutboundAsync.
/// </summary>
file sealed class SendSuccessStub(DwbHub.Core.Messaging.Message result) : IMessageService
{
    public Task<DwbHub.Core.Messaging.Message?> PersistInboundAsync(
        MessageReceivedEvent evt, CancellationToken ct = default)
        => Task.FromResult<DwbHub.Core.Messaging.Message?>(null);

    public Task PersistEditAsync(MessageUpdatedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MarkDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<DwbHub.Core.Messaging.Message> SendOutboundAsync(
        long tenantId, long channelId, long userId, string displayName,
        string content, CancellationToken ct = default)
        => Task.FromResult(result);

    public Task<IReadOnlyList<DwbHub.Core.Messaging.Message>> ListHistoryAsync(
        long tenantId, long channelId, long? beforeSnowflake, int limit,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DwbHub.Core.Messaging.Message>>(
            Array.Empty<DwbHub.Core.Messaging.Message>());
}

/// <summary>
/// Stub that throws a specified exception from SendOutboundAsync.
/// Delegates GET history to in-memory empty list.
/// </summary>
file sealed class ThrowOnSendStub(Exception exToThrow) : IMessageService
{
    public Task<DwbHub.Core.Messaging.Message?> PersistInboundAsync(
        MessageReceivedEvent evt, CancellationToken ct = default)
        => Task.FromResult<DwbHub.Core.Messaging.Message?>(null);

    public Task PersistEditAsync(MessageUpdatedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MarkDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<DwbHub.Core.Messaging.Message> SendOutboundAsync(
        long tenantId, long channelId, long userId, string displayName,
        string content, CancellationToken ct = default)
        => Task.FromException<DwbHub.Core.Messaging.Message>(exToThrow);

    public Task<IReadOnlyList<DwbHub.Core.Messaging.Message>> ListHistoryAsync(
        long tenantId, long channelId, long? beforeSnowflake, int limit,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DwbHub.Core.Messaging.Message>>(
            Array.Empty<DwbHub.Core.Messaging.Message>());
}
