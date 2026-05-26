using System.Data;
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
using DwbHub.Infrastructure.Messaging;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Messaging;

/// <summary>
/// Integration tests for PATCH /api/t/{slug}/channels/{channelPublicId}/messages/{messageId}.
/// Uses the real MessageService wired to a capturing fake Discord client + live Testcontainer DB.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MessagesControllerPatchTests : IAsyncLifetime
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
    private readonly MessageRepository _msgRepo;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;
    private readonly AesGcmChannelWebhookCipher _cipher;

    private WebApplicationFactory<Program> _factory = null!;
    private CapturingDiscordFake _discordFake = null!;

    public MessagesControllerPatchTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new PatchDateTimeOffsetHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var fac = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(fac);
        _users = new UserRepository(fac);
        _guilds = new GuildRepository(fac);
        _channels = new GuildChannelRepository(fac);
        _webhooks = new ChannelWebhookRepository(fac);
        _msgRepo = new MessageRepository(fac);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64JwtKey);
        _cipher = new AesGcmChannelWebhookCipher(Base64EncKey);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        _discordFake = new CapturingDiscordFake();
        var uniqueLogDir = Path.Combine(Path.GetTempPath(), "dwbhub-patch-test-logs", Guid.NewGuid().ToString("N"));
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
        string slug, string email, string displayName = "Owner")
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

    private async Task<(long guildId, long channelId, Guid channelPublicId)> SeedBridgedSetupAsync(
        long tenantId, long userId, long discordChannelId = 100000000000010001L)
    {
        var (gid, _) = await _guilds.CreateAsync(tenantId, "100000000000010111", "TestGuild", userId);
        var channel = await _channels.UpsertFromSyncAsync(tenantId, gid, discordChannelId, "general", 0, 0);
        await _channels.SetBridgedAsync(tenantId, channel.PublicId, true);
        var updated = await _channels.GetByPublicIdAsync(tenantId, channel.PublicId);
        return (gid, updated!.Id, updated.PublicId);
    }

    private async Task<long> SeedWebhookAsync(long tenantId, long channelId, long userId,
        long discordWebhookId = 900000000000010001L, string token = "fake-webhook-token")
    {
        var envelope = _cipher.Encrypt(token);
        await _webhooks.InsertAsync(new ChannelWebhook
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordWebhookId = discordWebhookId,
            Ciphertext = envelope.Ciphertext,
            Nonce = envelope.Nonce,
            AuthTag = envelope.AuthTag,
            KeyVersion = envelope.KeyVersion,
            CreatedByUserId = userId,
        });
        return discordWebhookId;
    }

    private async Task<Message> SeedOutboundMessageAsync(long tenantId, long channelId, long userId,
        long snowflake = 800000000000010001L, string content = "Original content",
        bool deleted = false)
    {
        var msg = new Message
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordMessageId = snowflake,
            DiscordAuthorId = 1L,
            DiscordAuthorName = "Owner",
            ViaDwbhub = true,
            DwbhubUserId = userId,
            Content = content,
            SentAt = DateTimeOffset.UtcNow,
        };
        var inserted = await _msgRepo.InsertAsync(msg);
        if (deleted)
            await _msgRepo.SoftDeleteByIdAsync(tenantId, inserted!.Id, DateTimeOffset.UtcNow);
        return inserted!;
    }

    private HttpClient BuildClient(string jwt)
    {
        var connectionFactory = new NpgsqlConnectionFactory(_ds);
        var fake = _discordFake;

        return _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                svc.RemoveAll<IDiscordRestChannelClient>();
                svc.AddSingleton<IDiscordRestChannelClient>(fake);
            }))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    private static string PatchUrl(string slug, Guid channelPublicId, long messageId) =>
        $"/api/t/{slug}/channels/{channelPublicId:D}/messages/{messageId}";

    // ── 1. Happy path → 200 with updated MessageDto ──────────────────────────

    [Fact]
    public async Task Patch_happy_path_returns_200_updates_db_emits_audit_broadcasts()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("patch-happy", "ph@test.local");
        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid);
        await SeedWebhookAsync(tid, channelId, uid);
        var msg = await SeedOutboundMessageAsync(tid, channelId, uid);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.PatchAsJsonAsync(
            PatchUrl("patch-happy", channelPublicId, msg.Id),
            new { content = "Updated content" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("content").GetString().Should().Be("Updated content");
        body.GetProperty("editedAt").GetString().Should().NotBeNullOrEmpty();

        // DB row must have updated content + edited_at set.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var row = await conn.QuerySingleAsync(
            "SELECT content, edited_at FROM messages WHERE id = @id",
            new { id = msg.Id });
        ((string)row.content).Should().Be("Updated content");
        ((DateTimeOffset?)row.edited_at).Should().NotBeNull();

        // Audit log must have message.edit.self event for this tenant.
        var auditCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE tenant_id = @tid AND event_type = 'message.edit.self'",
            new { tid });
        auditCount.Should().BeGreaterThanOrEqualTo(1);

        // Discord fake must have recorded an EditWebhook call.
        _discordFake.EditWebhookCalls.Should().ContainSingle();
        _discordFake.EditWebhookCalls[0].NewContent.Should().Be("Updated content");
    }

    // ── 2. Cross-tenant → 404 ────────────────────────────────────────────────

    [Fact]
    public async Task Patch_cross_tenant_returns_404()
    {
        var (_, uidA, jwtA) = await SeedOwnerAsync("patch-xta", "pxa@test.local");
        var (tidB, uidB, _) = await SeedOwnerAsync("patch-xtb", "pxb@test.local");
        var (_, channelIdB, channelPublicIdB) = await SeedBridgedSetupAsync(tidB, uidB, 100000000000010201L);
        await SeedWebhookAsync(tidB, channelIdB, uidB, 900000000000010201L);
        var msgB = await SeedOutboundMessageAsync(tidB, channelIdB, uidB, 800000000000010201L);

        using var client = BuildClient(jwtA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtA);

        // Tenant A's JWT targeting tenant B's message, via tenant A's slug.
        var res = await client.PatchAsJsonAsync(
            PatchUrl("patch-xta", channelPublicIdB, msgB.Id),
            new { content = "Cross-tenant attack" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-tenant edit must return 404 to prevent information disclosure");

        // No audit event in either tenant.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var auditCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE event_type = 'message.edit.self'");
        auditCount.Should().Be(0);
    }

    // ── 3. Deleted message → 410 ─────────────────────────────────────────────

    [Fact]
    public async Task Patch_deleted_message_returns_410()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("patch-del", "pd@test.local");
        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000010301L);
        await SeedWebhookAsync(tid, channelId, uid, 900000000000010301L);
        var msg = await SeedOutboundMessageAsync(tid, channelId, uid, 800000000000010301L, deleted: true);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.PatchAsJsonAsync(
            PatchUrl("patch-del", channelPublicId, msg.Id),
            new { content = "Edit a deleted message" });

        res.StatusCode.Should().Be(HttpStatusCode.Gone);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("already_deleted");
    }

    // ── 4. Outside edit window → 422 with typed body ─────────────────────────

    [Fact]
    public async Task Patch_outside_edit_window_returns_422_with_typed_body()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("patch-wind", "pw@test.local");
        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000010401L);
        await SeedWebhookAsync(tid, channelId, uid, 900000000000010401L);

        // Set tenant edit window to 60 seconds.
        await _tenants.UpdateMessageEditWindowAsync(tid, 60, CancellationToken.None);

        // Seed a message that appears 120 seconds old by setting sent_at in the past.
        var msg = await SeedOutboundMessageAsync(tid, channelId, uid, 800000000000010401L, "Old message");
        await using var setupConn = _ds.CreateConnection();
        await setupConn.OpenAsync();
        await setupConn.ExecuteAsync(
            "UPDATE messages SET sent_at = @t WHERE id = @id",
            new { t = DateTimeOffset.UtcNow.AddSeconds(-120), id = msg.Id });

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.PatchAsJsonAsync(
            PatchUrl("patch-wind", channelPublicId, msg.Id),
            new { content = "Too late to edit" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("edit_window_expired");
        body.GetProperty("windowSeconds").GetInt32().Should().Be(60);
        body.GetProperty("ageSeconds").GetInt32().Should().BeGreaterThanOrEqualTo(60);
    }

    // ── 5. Empty content → 400 ───────────────────────────────────────────────

    [Fact]
    public async Task Patch_empty_content_returns_400()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("patch-empty", "pe@test.local");
        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000010501L);
        await SeedWebhookAsync(tid, channelId, uid, 900000000000010501L);
        var msg = await SeedOutboundMessageAsync(tid, channelId, uid, 800000000000010501L);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.PatchAsJsonAsync(
            PatchUrl("patch-empty", channelPublicId, msg.Id),
            new { content = "   " });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 6. Content too long → 400 ────────────────────────────────────────────

    [Fact]
    public async Task Patch_too_long_returns_400()
    {
        var (tid, uid, jwt) = await SeedOwnerAsync("patch-long", "pl@test.local");
        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000010601L);
        await SeedWebhookAsync(tid, channelId, uid, 900000000000010601L);
        var msg = await SeedOutboundMessageAsync(tid, channelId, uid, 800000000000010601L);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.PatchAsJsonAsync(
            PatchUrl("patch-long", channelPublicId, msg.Id),
            new { content = new string('x', 2001) });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

// ── File-scoped DateTimeOffset handler for this test class ───────────────────

file sealed class PatchDateTimeOffsetHandler : Dapper.SqlMapper.TypeHandler<DateTimeOffset>
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
