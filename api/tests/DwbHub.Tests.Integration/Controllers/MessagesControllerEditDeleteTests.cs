using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using DwbHub.Application.Audit;
using DwbHub.Application.Encryption;
using DwbHub.Application.Messaging;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Encryption;
using DwbHub.Infrastructure.Messaging;
using DwbHub.Tests.Integration.Infrastructure;
using DwbHub.Tests.Shared.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for the user-facing
/// PATCH  /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}
/// DELETE /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}
/// endpoints.
///
/// Uses the real <see cref="MessageService"/> and the real DB; Discord calls are
/// intercepted by <see cref="FakeDiscordRestChannelClient"/> (DI override).
/// Webhooks are seeded with a real AES-GCM envelope so the cipher round-trip works.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MessagesControllerEditDeleteTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _channels;
    private readonly ChannelWebhookRepository _webhooks;
    private readonly GuildBotCredentialRepository _botCreds;
    private readonly MessageRepository _msgRepo;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;
    private readonly AesGcmChannelWebhookCipher _cipher;
    private readonly AesGcmBotTokenEncryptor _botEncryptor;

    private WebApplicationFactory<Program> _factory = null!;

    public MessagesControllerEditDeleteTests(PostgresFixture fixture)
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
        _webhooks = new ChannelWebhookRepository(fac);
        _botCreds = new GuildBotCredentialRepository(fac);
        _msgRepo = new MessageRepository(fac);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64JwtKey);
        _cipher = new AesGcmChannelWebhookCipher(Base64EncKey);
        _botEncryptor = new AesGcmBotTokenEncryptor(Base64EncKey);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var logDir = Path.Combine(Path.GetTempPath(), "dwbhub-edit-del-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DWBHUB_LOG_DIR", logDir);
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

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task<(long tenantId, long userId, string jwt)> SeedUserAsync(
        string slug, string email, string displayName, UserRole role = UserRole.Member)
    {
        var tid = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: displayName,
            Role: role, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", displayName, role, true, default, default);
        var tenant = new Tenant(tid, $"T-{slug}", slug, "en", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    /// <summary>
    /// Adds a second user to an EXISTING tenant (no new tenant created).
    /// </summary>
    private async Task<(long userId, string jwt)> AddUserToTenantAsync(
        long tenantId, string slug, string email, string displayName, UserRole role)
    {
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tenantId, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: displayName,
            Role: role, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tenantId, email, DateTimeOffset.UtcNow, "", displayName, role, true, default, default);
        var tenant = new Tenant(tenantId, $"T-{slug}", slug, "en", default, default);
        return (uid, _issuer.Issue(user, tenant));
    }

    private async Task<(long channelId, Guid channelPublicId)> SeedBridgedChannelAsync(
        long tenantId, long guildId, long discordChannelId)
    {
        var ch = await _channels.UpsertFromSyncAsync(tenantId, guildId, discordChannelId, "general", 0, 0);
        await _channels.SetBridgedAsync(tenantId, ch.PublicId, true);
        var updated = await _channels.GetByPublicIdAsync(tenantId, ch.PublicId);
        return (updated!.Id, updated.PublicId);
    }

    /// <summary>
    /// Seeds a webhook for the given channel using the real AES cipher.
    /// The FakeDiscordRestChannelClient will accept any webhook id/token.
    /// </summary>
    private async Task SeedWebhookAsync(long tenantId, long channelId, long userId)
    {
        var envelope = _cipher.Encrypt("fake-webhook-token-do-not-leak");
        await _webhooks.InsertAsync(new ChannelWebhook
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordWebhookId = 900000000000000001L,
            Ciphertext = envelope.Ciphertext,
            Nonce = envelope.Nonce,
            AuthTag = envelope.AuthTag,
            KeyVersion = envelope.KeyVersion,
            CreatedByUserId = userId,
        });
    }

    /// <summary>
    /// Seeds bot credentials for a guild using the test encryptor.
    /// Required for inbound-delete tests so MessageService can decrypt the bot token.
    /// The actual token value is irrelevant — FakeDiscordRestChannelClient accepts any token.
    /// </summary>
    private async Task SeedBotCredentialsAsync(long guildId, long tenantId)
    {
        // The guild_bot_credentials CHECK constraint requires ciphertext BETWEEN 50 AND 200 bytes.
        // AES-GCM ciphertext length = plaintext length, so we need a plaintext of at least 50 chars.
        // Real Discord bot tokens are 70+ chars, so this is representative.
        const string fakeBotToken =
            "FAKE_BOT_TOKEN_FOR_INTEGRATION_TESTING_ONLY.AAAAAAAAAAAAAAAAAAAAAAAAA";
        var envelope = _botEncryptor.Encrypt(fakeBotToken);
        await _botCreds.UpsertAsync(guildId, tenantId, envelope);
    }

    /// <summary>
    /// Inserts a via_dwbhub=false (inbound) message, e.g. one posted by an external bot
    /// or user directly in the Discord channel (not via our webhook).
    /// Returns the persisted row (with public_id).
    /// </summary>
    private async Task<Message> SeedInboundMessageAsync(
        long tenantId, long channelId,
        long snowflake, string content = "Inbound message from Discord",
        DateTimeOffset? sentAt = null)
    {
        var msg = new Message
        {
            TenantId = tenantId,
            ChannelId = channelId,
            // Snowflake must be a valid 18-digit Discord-like value
            DiscordMessageId = snowflake,
            DiscordAuthorId = 300000000000000099L, // external author — not a dwbhub user
            DiscordAuthorName = "ExternalUser",
            ViaDwbhub = false,
            DwbhubUserId = null, // inbound messages have no dwbhub user
            Content = content,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
        };
        return (await _msgRepo.InsertAsync(msg))!;
    }

    /// <summary>
    /// Inserts a via_dwbhub=true message authored by <paramref name="authorUserId"/>.
    /// Returns the persisted row (with public_id).
    /// </summary>
    private async Task<Message> SeedOutboundMessageAsync(
        long tenantId, long channelId, long authorUserId,
        long snowflake, string content = "Hello edit me",
        DateTimeOffset? sentAt = null)
    {
        var msg = new Message
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordMessageId = snowflake,
            DiscordAuthorId = 900000000000000002L,
            DiscordAuthorName = "Author",
            ViaDwbhub = true,
            DwbhubUserId = authorUserId,
            Content = content,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
        };
        return (await _msgRepo.InsertAsync(msg))!;
    }

    /// <summary>
    /// Builds an HTTP client wired to the test DB, real MessageService,
    /// and FakeDiscordRestChannelClient.
    /// </summary>
    private HttpClient BuildClient(string jwt)
        => BuildClientWithFakeTracking(jwt, out _);

    /// <summary>
    /// Builds an HTTP client and also outputs the <see cref="FakeDiscordRestChannelClient"/>
    /// singleton so callers can inspect <see cref="FakeDiscordRestChannelClient.BotDeleteCalls"/>
    /// and <see cref="FakeDiscordRestChannelClient.WebhookDeleteCalls"/> after the request.
    /// </summary>
    private HttpClient BuildClientWithFakeTracking(string jwt, out FakeDiscordRestChannelClient fake)
    {
        var connFac = new NpgsqlConnectionFactory(_ds);
        // Use a minimal IHostEnvironment that reports "Development" (non-Production)
        // so FakeDiscordRestChannelClient's production guard does not throw.
        var sharedFake = new FakeDiscordRestChannelClient(
            new TestHostEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FakeDiscordRestChannelClient>.Instance);
        fake = sharedFake;

        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                // Real repos against the test DB.
                svc.RemoveAll<IMessageRepository>();
                svc.AddScoped<IMessageRepository>(_ => new MessageRepository(connFac));
                svc.RemoveAll<IGuildChannelRepository>();
                svc.AddScoped<IGuildChannelRepository>(_ => new GuildChannelRepository(connFac));
                svc.RemoveAll<IChannelWebhookRepository>();
                svc.AddScoped<IChannelWebhookRepository>(_ => new ChannelWebhookRepository(connFac));
                svc.RemoveAll<IUserRepository>();
                svc.AddScoped<IUserRepository>(_ => new UserRepository(connFac));
                svc.RemoveAll<IGuildBotCredentialRepository>();
                svc.AddScoped<IGuildBotCredentialRepository>(_ => new GuildBotCredentialRepository(connFac));

                // Fake Discord (no real HTTP calls) — shared instance for call tracking.
                svc.RemoveAll<IDiscordRestChannelClient>();
                svc.AddSingleton<IDiscordRestChannelClient>(sharedFake);

                // Real encryptor so bot-token decrypt round-trips work.
                svc.RemoveAll<IBotTokenEncryptor>();
                svc.AddSingleton<IBotTokenEncryptor>(new AesGcmBotTokenEncryptor(Base64EncKey));

                // Real MessageService (uses the real repos above).
                svc.RemoveAll<IMessageService>();
                svc.AddScoped<IMessageService, MessageService>();

                // No-op broadcaster (SignalR not needed in unit tests).
                svc.RemoveAll<IMessagesBroadcaster>();
                svc.AddSingleton<IMessagesBroadcaster>(new NoOpMessagesBroadcasterForEditTests());
            }))
            .CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // ── PATCH tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PATCH_Message_AsAuthor_Returns200_WithUpdatedContent()
    {
        var (tid, uid, jwt) = await SeedUserAsync("edit-happy", "author@edit.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000211", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020001L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000001L);

        using var client = BuildClient(jwt);
        var res = await client.PatchMessageAsync("edit-happy", cpid, msg.PublicId,
            new { content = "Edited content" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("content").GetString().Should().Be("Edited content");
        body.GetProperty("publicId").GetString().Should().Be(msg.PublicId.ToString("D"));
        body.GetProperty("editedAt").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PATCH_Message_AsOtherUser_Returns403()
    {
        var (tid, uid, _) = await SeedUserAsync("edit-403", "author@edit403.test", "Author", UserRole.Member);
        var (otherId, otherJwt) = await AddUserToTenantAsync(tid, "edit-403", "other@edit403.test", "Other", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000212", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020002L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000002L);

        using var client = BuildClient(otherJwt);
        var res = await client.PatchMessageAsync("edit-403", cpid, msg.PublicId,
            new { content = "Sneaky edit" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PATCH_Message_AfterTenMinutes_Returns422_EditWindowExpired()
    {
        var (tid, uid, jwt) = await SeedUserAsync("edit-window", "author@editwindow.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000213", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020003L);
        await SeedWebhookAsync(tid, cid, uid);
        // SentAt = 11 minutes ago → beyond the 10-minute window.
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000003L,
            sentAt: DateTimeOffset.UtcNow.AddMinutes(-11));

        using var client = BuildClient(jwt);
        var res = await client.PatchMessageAsync("edit-window", cpid, msg.PublicId,
            new { content = "Too late" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("edit_window_expired");
    }

    [Fact]
    public async Task PATCH_Message_WithTooLongContent_Returns400()
    {
        var (tid, uid, jwt) = await SeedUserAsync("edit-toolong", "author@edittoolong.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000214", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020004L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000004L);

        using var client = BuildClient(jwt);
        var res = await client.PatchMessageAsync("edit-toolong", cpid, msg.PublicId,
            new { content = new string('x', 1801) });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PATCH_Message_WithEmptyContent_Returns400()
    {
        var (tid, uid, jwt) = await SeedUserAsync("edit-empty", "author@editempty.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000215", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020005L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000005L);

        using var client = BuildClient(jwt);
        var res = await client.PatchMessageAsync("edit-empty", cpid, msg.PublicId,
            new { content = "" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PATCH_Message_WithWhitespaceOnly_Returns400()
    {
        var (tid, uid, jwt) = await SeedUserAsync("edit-ws", "author@editws.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000216", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020006L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000006L);

        using var client = BuildClient(jwt);
        var res = await client.PatchMessageAsync("edit-ws", cpid, msg.PublicId,
            new { content = "   " });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("content_required");
    }

    [Fact]
    public async Task PATCH_Message_NotFound_Returns404()
    {
        var (tid, uid, jwt) = await SeedUserAsync("edit-404", "author@edit404.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000217", "Guild", uid)).Item1;
        var (_, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020007L);

        using var client = BuildClient(jwt);
        var res = await client.PatchMessageAsync("edit-404", cpid, Guid.NewGuid(),
            new { content = "Ghost edit" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH audit — content must NOT appear in audit payload ───────────────

    [Fact]
    public async Task PATCH_Message_AuditLog_DoesNotContainContent()
    {
        var (tid, uid, jwt) = await SeedUserAsync("edit-audit", "author@editaudit.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000218", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020008L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000008L, "sensitive content");

        using var client = BuildClient(jwt);
        var res = await client.PatchMessageAsync("edit-audit", cpid, msg.PublicId,
            new { content = "updated secret content" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        // Query audit_log for message.edited events and verify "content" key is absent.
        await using var conn = await _ds.OpenConnectionAsync();
        var payload = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT payload_json::text FROM audit_log WHERE event_type = 'message.edited' AND tenant_id = @Tid ORDER BY id DESC LIMIT 1",
            new { Tid = tid });
        payload.Should().NotBeNull();
        payload.Should().NotContain("updated secret content");
        payload.Should().NotContain("sensitive content");
    }

    // ── DELETE tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DELETE_Message_AsAuthor_Returns204_AndSoftDeletes()
    {
        var (tid, uid, jwt) = await SeedUserAsync("del-happy", "author@delhappy.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000221", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020011L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000011L);

        using var client = BuildClient(jwt);
        var res = await client.DeleteMessageAsync("del-happy", cpid, msg.PublicId);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // DB: row should be soft-deleted (deleted_at IS NOT NULL).
        await using var conn = await _ds.OpenConnectionAsync();
        var deletedAt = await conn.QuerySingleOrDefaultAsync<DateTimeOffset?>(
            "SELECT deleted_at FROM messages WHERE id = @Id", new { Id = msg.Id });
        deletedAt.Should().NotBeNull("soft-delete must set deleted_at");
    }

    [Fact]
    public async Task DELETE_Message_AsOwner_NotAuthor_Returns204()
    {
        var (tid, authorId, _) = await SeedUserAsync("del-owner", "author@delowner.test", "Author", UserRole.Member);
        var (ownerId, ownerJwt) = await AddUserToTenantAsync(tid, "del-owner", "owner@delowner.test", "Owner", UserRole.Owner);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000222", "Guild", authorId)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020012L);
        await SeedWebhookAsync(tid, cid, authorId);
        var msg = await SeedOutboundMessageAsync(tid, cid, authorId, 500000000000000012L);

        using var client = BuildClient(ownerJwt);
        var res = await client.DeleteMessageAsync("del-owner", cpid, msg.PublicId);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DELETE_Message_AsOtherMember_Returns403()
    {
        var (tid, uid, _) = await SeedUserAsync("del-403", "author@del403.test", "Author", UserRole.Member);
        var (otherId, otherJwt) = await AddUserToTenantAsync(tid, "del-403", "other@del403.test", "Other", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000223", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020013L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000013L);

        using var client = BuildClient(otherJwt);
        var res = await client.DeleteMessageAsync("del-403", cpid, msg.PublicId);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DELETE_Message_NotFound_Returns404()
    {
        var (tid, uid, jwt) = await SeedUserAsync("del-404", "author@del404.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000224", "Guild", uid)).Item1;
        var (_, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020014L);

        using var client = BuildClient(jwt);
        var res = await client.DeleteMessageAsync("del-404", cpid, Guid.NewGuid());

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DELETE_Message_Idempotent_SecondDelete_Returns404()
    {
        // Convention: a second DELETE on an already-soft-deleted message returns 404
        // (the row is no longer "findable" from the user's perspective).
        var (tid, uid, jwt) = await SeedUserAsync("del-idem", "author@delidem.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000225", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020015L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000015L);

        using var client = BuildClient(jwt);

        // First delete — should succeed.
        var res1 = await client.DeleteMessageAsync("del-idem", cpid, msg.PublicId);
        res1.StatusCode.Should().Be(HttpStatusCode.NoContent, "first delete should succeed");

        // Second delete — should return 404 (already gone).
        var res2 = await client.DeleteMessageAsync("del-idem", cpid, msg.PublicId);
        res2.StatusCode.Should().Be(HttpStatusCode.NotFound, "second delete on soft-deleted row should return 404");
    }

    // ── DELETE inbound message tests ──────────────────────────────────────────

    [Fact]
    public async Task Delete_Message_AsOwner_Inbound_Returns204_AndCallsBotDelete()
    {
        // Arrange: owner + guild + bridged channel + bot credentials + inbound message.
        var (tid, authorId, _) = await SeedUserAsync("del-inbound-owner", "author@delinbound.test", "Author", UserRole.Member);
        var (ownerId, ownerJwt) = await AddUserToTenantAsync(tid, "del-inbound-owner", "owner@delinbound.test", "Owner", UserRole.Owner);
        var (gid, _) = await _guilds.CreateAsync(tid, "100000000000000231", "Guild", authorId);
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020031L);
        await SeedBotCredentialsAsync(gid, tid);
        // No webhook seed — inbound messages don't go through our webhook.
        var msg = await SeedInboundMessageAsync(tid, cid, 500000000000000031L);

        using var client = BuildClientWithFakeTracking(ownerJwt, out var fake);

        // Act.
        var res = await client.DeleteMessageAsync("del-inbound-owner", cpid, msg.PublicId);

        // Assert: HTTP 204.
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert: bot-token delete was called (not webhook delete).
        fake.BotDeleteCalls.Should().ContainSingle(
            call => call.MessageId == (ulong)msg.DiscordMessageId,
            "owner deleting an inbound message must route to DeleteMessageAsync (bot-token path)");
        fake.WebhookDeleteCalls.Should().BeEmpty(
            "inbound messages must NOT go through the webhook delete path");

        // Assert: DB soft-delete.
        await using var conn = await _ds.OpenConnectionAsync();
        var deletedAt = await conn.QuerySingleOrDefaultAsync<DateTimeOffset?>(
            "SELECT deleted_at FROM messages WHERE id = @Id", new { Id = msg.Id });
        deletedAt.Should().NotBeNull("soft-delete must set deleted_at");
    }

    [Fact]
    public async Task Delete_Message_AsOtherMember_Inbound_Returns403()
    {
        // Arrange: two members (neither is owner), inbound message from an external user.
        var (tid, authorId, _) = await SeedUserAsync("del-inbound-403", "author@delinbound403.test", "Author", UserRole.Member);
        var (otherId, otherJwt) = await AddUserToTenantAsync(tid, "del-inbound-403", "other@delinbound403.test", "Other", UserRole.Member);
        var (gid, _) = await _guilds.CreateAsync(tid, "100000000000000232", "Guild", authorId);
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020032L);
        var msg = await SeedInboundMessageAsync(tid, cid, 500000000000000032L);

        using var client = BuildClient(otherJwt);

        var res = await client.DeleteMessageAsync("del-inbound-403", cpid, msg.PublicId);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a regular member cannot delete someone else's inbound message");
    }

    [Fact]
    public async Task Delete_Message_AsAuthor_Of_Inbound_Cannot_Delete()
    {
        // Inbound messages (ViaDwbhub=false) have DwbhubUserId=null — no dwbhub user is the
        // "author" in our system. This means isAuthor check (DwbhubUserId == actorUserId) will
        // always be false for a non-null actorUserId. Only Owners can delete inbound messages.
        // This test asserts that a regular member who happened to post the inbound message
        // (from Discord's side) cannot delete it via our API.
        var (tid, memberId, memberJwt) = await SeedUserAsync("del-inbound-noauthor", "member@delinbound.test", "Member", UserRole.Member);
        var (gid, _) = await _guilds.CreateAsync(tid, "100000000000000233", "Guild", memberId);
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020033L);
        // Inbound: DwbhubUserId is null — no member can claim authorship.
        var msg = await SeedInboundMessageAsync(tid, cid, 500000000000000033L);

        using var client = BuildClient(memberJwt);

        var res = await client.DeleteMessageAsync("del-inbound-noauthor", cpid, msg.PublicId);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "members cannot delete inbound messages even if they were the Discord sender — " +
            "only Owners can perform moderation deletes via the API");
    }

    // ── DELETE audit — content must NOT appear in audit payload ──────────────

    [Fact]
    public async Task DELETE_Message_AuditLog_DoesNotContainContent()
    {
        var (tid, uid, jwt) = await SeedUserAsync("del-audit", "author@delaudit.test", "Author", UserRole.Member);
        var gid = (await _guilds.CreateAsync(tid, "100000000000000226", "Guild", uid)).Item1;
        var (cid, cpid) = await SeedBridgedChannelAsync(tid, gid, 100000000000020016L);
        await SeedWebhookAsync(tid, cid, uid);
        var msg = await SeedOutboundMessageAsync(tid, cid, uid, 500000000000000016L, "very sensitive content");

        using var client = BuildClient(jwt);
        var res = await client.DeleteMessageAsync("del-audit", cpid, msg.PublicId);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var conn = await _ds.OpenConnectionAsync();
        var payload = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT payload_json::text FROM audit_log WHERE event_type = 'message.deleted' AND tenant_id = @Tid ORDER BY id DESC LIMIT 1",
            new { Tid = tid });
        payload.Should().NotBeNull();
        payload.Should().NotContain("very sensitive content");
    }
}

// ── Minimal IHostEnvironment for test use ─────────────────────────────────────

/// <summary>
/// Reports "Development" environment so FakeDiscordRestChannelClient's
/// production guard (env.IsProduction() check) does not throw in tests.
/// </summary>
file sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "DwbHub.Tests";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
        = new Microsoft.Extensions.FileProviders.NullFileProvider();
}

// ── No-op broadcaster for edit/delete tests ───────────────────────────────────

file sealed class NoOpMessagesBroadcasterForEditTests : IMessagesBroadcaster
{
    public Task MessageReceivedAsync(MessageBroadcastDto msg, Guid channelPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageUpdatedAsync(long tenantId, long messageId, string content, DateTimeOffset editedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageDeletedAsync(long tenantId, long messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task BackfillProgressAsync(long tenantId, Guid channelPublicId, long jobId, int fetchedCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task BackfillCompleteAsync(long tenantId, Guid channelPublicId, long jobId, int fetchedCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChannelBridgeChangedAsync(long tenantId, Guid channelPublicId, bool isBridged, CancellationToken ct = default) => Task.CompletedTask;
}
