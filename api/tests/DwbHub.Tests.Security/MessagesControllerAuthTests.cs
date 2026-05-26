using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using DwbHub.Application.Messaging;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Security;

/// <summary>
/// Security-focused tests for MessagesController (Plan 1.0 Task 9).
///
/// Threat model:
///   S1: a client for tenant A attempts to send a message to tenant B's channel —
///       must receive 404 (not 403) so no tenant membership is disclosed.
///   S2: message content containing HTML / Discord markdown must be stored as-is
///       (raw text). The API must not escape or strip content on write. XSS
///       prevention is the frontend's responsibility (Plan 1.5).
/// </summary>
[Collection(SecurityDatabaseCollection.Name)]
public sealed class MessagesControllerAuthTests : IAsyncLifetime
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

    public MessagesControllerAuthTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new SecurityDateTimeOffsetHandler());
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
        _factory = new WebApplicationFactory<Program>();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        _ds.Dispose();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(long tenantId, long userId, string jwt)> SeedOwnerAsync(
        string slug, string email, string displayName = "SecUser")
    {
        var tid = await _tenants.CreateAsync(name: $"Sec-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: displayName,
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", displayName,
            UserRole.Owner, true, default, default);
        var tenant = new Tenant(tid, $"Sec-{slug}", slug, "en", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    private async Task<Guid> SeedBridgedChannelPublicIdAsync(long tenantId, long guildId,
        long discordChannelId)
    {
        var channel = await _channels.UpsertFromSyncAsync(
            tenantId, guildId, discordChannelId, "sec-channel", 0, 0);
        await _channels.SetBridgedAsync(tenantId, channel.PublicId, true);
        return channel.PublicId;
    }

    private HttpClient BuildClient(string jwt)
    {
        var connectionFactory = new NpgsqlConnectionFactory(_ds);
        return _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                svc.RemoveAll<IGuildChannelRepository>();
                svc.AddScoped<IGuildChannelRepository>(_ =>
                    new GuildChannelRepository(connectionFactory));
                svc.RemoveAll<IMessageService>();
                svc.AddScoped<IMessageService>(_ =>
                    new SecTestMessageService(_msgRepo));
            }))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    // ── Security test S1: cross-tenant channel access returns 404, not 403 ───

    [Fact]
    public async Task POST_TenantA_TargetingChannel_OwnedByTenantB_Returns404_NotForbidden()
    {
        // Arrange: two separate tenants, each with a bridged channel.
        var (_, uidA, jwtA) = await SeedOwnerAsync("sec-t1a", "a@sec.local", "UserA");
        var (tidB, uidB, _) = await SeedOwnerAsync("sec-t1b", "b@sec.local", "UserB");

        var (gidB, _) = await _guilds.CreateAsync(tidB, "200000000000000111", "GuildB", uidB);
        var channelBPublicId = await SeedBridgedChannelPublicIdAsync(tidB, gidB, 200000000000000999L);

        using var client = BuildClient(jwtA);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwtA);

        // Tenant A authenticated, targeting tenant B's channel via tenant A's slug.
        var res = await client.PostAsJsonAsync(
            $"/api/t/sec-t1a/channels/{channelBPublicId:D}/messages",
            new { content = "attack" });

        // Must be 404 — must NOT be 403 (which would reveal the channel exists in another tenant).
        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-tenant channel access must return 404 (not 403) to prevent information disclosure; " +
            "a 403 would confirm to the attacker that the channel UUID is valid in some tenant");

        // Verify the response body contains no information about tenant B.
        var responseBody = await res.Content.ReadAsStringAsync();
        responseBody.Should().NotContain("sec-t1b",
            "error response must not disclose the other tenant's slug");
        responseBody.Should().NotContain("UserB",
            "error response must not disclose the other tenant's user data");
    }

    // ── Security test S2: content injection stored as raw text ───────────────

    [Fact]
    public async Task POST_ContentInjection_DiscordMarkdownHtml_PersistedAsRawText()
    {
        // Arrange: content contains HTML, Discord markdown, and a javascript: URI.
        const string injectedContent =
            "<script>alert('xss')</script> **bold** [hidden](javascript:x)";

        var (tid, uid, jwt) = await SeedOwnerAsync("sec-xss", "xss@sec.local", "XssUser");
        var (gid, _) = await _guilds.CreateAsync(tid, "300000000000000111", "GuildXss", uid);
        var channelPublicId = await SeedBridgedChannelPublicIdAsync(tid, gid, 300000000000000999L);

        // Use a spy that records what content was actually passed to SendOutboundAsync.
        var capturedContent = "";
        var spy = new SendSpyMessageService(
            onSend: (_, _, _, _, content) => { capturedContent = content; },
            msgRepo: _msgRepo);

        using var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                svc.RemoveAll<IGuildChannelRepository>();
                svc.AddScoped<IGuildChannelRepository>(_ =>
                    new GuildChannelRepository(new NpgsqlConnectionFactory(_ds)));
                svc.RemoveAll<IMessageService>();
                svc.AddScoped<IMessageService>(_ => spy);
            }))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt);

        // Act: POST with the injection payload.
        var res = await client.PostAsJsonAsync(
            $"/api/t/sec-xss/channels/{channelPublicId:D}/messages",
            new { content = injectedContent });

        res.StatusCode.Should().Be(HttpStatusCode.Created,
            "a valid (non-empty, ≤2000 chars) content must be accepted regardless of its character set");

        // Assert: the content passed to the service is the EXACT raw string.
        // The controller should not escape, strip, or encode the content before forwarding.
        // XSS prevention is the frontend's responsibility (Plan 1.5).
        capturedContent.Should().Be(injectedContent.Trim(),
            "the API must persist raw user input without HTML escaping, entity encoding, " +
            "or markdown stripping; the frontend owns XSS prevention (Plan 1.5)");

        // Additionally, the GET history endpoint must return the same raw string.
        // First seed the message directly (spy doesn't persist).
        var msg = new Message
        {
            TenantId = tid,
            ChannelId = await GetChannelIdAsync(tid, channelPublicId),
            DiscordMessageId = 300000000000010001L,
            DiscordAuthorId = 1L,
            DiscordAuthorName = "XssUser",
            ViaDwbhub = true,
            Content = injectedContent,
            SentAt = DateTimeOffset.UtcNow,
        };
        await _msgRepo.InsertAsync(msg);

        var histRes = await client.GetAsync(
            $"/api/t/sec-xss/channels/{channelPublicId:D}/messages");
        histRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var histBody = await histRes.Content.ReadAsStringAsync();
        // The raw content should appear in the response as-is (JSON string encoding is fine,
        // but no entity-escaped HTML like &lt;script&gt;).
        histBody.Should().Contain("<script>alert('xss')</script>",
            "the history GET must return the raw persisted content without HTML escaping; " +
            "the client/frontend is responsible for safe rendering");
    }

    private async Task<long> GetChannelIdAsync(long tenantId, Guid publicId)
    {
        var ch = await _channels.GetByPublicIdAsync(tenantId, publicId);
        return ch?.Id ?? throw new InvalidOperationException("Channel not found in test helper.");
    }
}

/// <summary>
/// Minimal IMessageService stub for security tests: passes through history reads,
/// records what was sent for assertion, returns a fake success message.
/// </summary>
file sealed class SecTestMessageService(IMessageRepository msgRepo) : IMessageService
{
    public Task<Message?> PersistInboundAsync(MessageReceivedEvent evt, CancellationToken ct = default)
        => Task.FromResult<Message?>(null);

    public Task PersistEditAsync(MessageUpdatedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MarkDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<Message> SendOutboundAsync(long tenantId, long channelId, long userId,
        string displayName, string content, CancellationToken ct = default)
    {
        var result = new Message
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
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Message>> ListHistoryAsync(
        long tenantId, long channelId, long? beforeSnowflake, int limit,
        CancellationToken ct = default)
        => msgRepo.ListByChannelBeforeAsync(tenantId, channelId, beforeSnowflake, limit, ct);
}

/// <summary>
/// IMessageService stub that invokes a callback on send, allowing tests to capture
/// the content and other arguments passed to the service layer.
/// </summary>
file sealed class SendSpyMessageService(
    Action<long, long, long, string, string> onSend,
    IMessageRepository msgRepo) : IMessageService
{
    public Task<Message?> PersistInboundAsync(MessageReceivedEvent evt, CancellationToken ct = default)
        => Task.FromResult<Message?>(null);

    public Task PersistEditAsync(MessageUpdatedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MarkDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<Message> SendOutboundAsync(long tenantId, long channelId, long userId,
        string displayName, string content, CancellationToken ct = default)
    {
        onSend(tenantId, channelId, userId, displayName, content);
        var result = new Message
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
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Message>> ListHistoryAsync(
        long tenantId, long channelId, long? beforeSnowflake, int limit,
        CancellationToken ct = default)
        => msgRepo.ListByChannelBeforeAsync(tenantId, channelId, beforeSnowflake, limit, ct);
}

/// <summary>
/// Local DateTimeOffset type handler for the security test assembly.
/// Mirrors the handler in DwbHub.Tests.Integration.Infrastructure.
/// </summary>
file sealed class SecurityDateTimeOffsetHandler : Dapper.SqlMapper.TypeHandler<DateTimeOffset>
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
