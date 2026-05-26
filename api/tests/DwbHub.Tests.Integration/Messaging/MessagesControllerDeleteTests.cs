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
/// Integration tests for DELETE /api/t/{slug}/channels/{channelPublicId}/messages/{messageId}.
/// Covers self-delete, mod-outbound, mod-inbound, cross-tenant, already-deleted, and forbidden paths.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MessagesControllerDeleteTests : IAsyncLifetime
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

    public MessagesControllerDeleteTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DeleteDateTimeOffsetHandler());
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
        var uniqueLogDir = Path.Combine(Path.GetTempPath(), "dwbhub-delete-test-logs", Guid.NewGuid().ToString("N"));
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

    private async Task<(long tenantId, long userId, string jwt)> SeedUserAsync(
        string slug, string email, string displayName, UserRole role)
    {
        var tid = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: displayName,
            Role: role, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", displayName,
            role, true, default, default);
        var tenant = new Tenant(tid, $"T-{slug}", slug, "en", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    private async Task<(long guildId, long channelId, Guid channelPublicId)> SeedBridgedSetupAsync(
        long tenantId, long userId, long discordChannelId)
    {
        var (gid, _) = await _guilds.CreateAsync(tenantId, "100000000000020111", "TestGuild", userId);
        var channel = await _channels.UpsertFromSyncAsync(tenantId, gid, discordChannelId, "general", 0, 0);
        await _channels.SetBridgedAsync(tenantId, channel.PublicId, true);
        var updated = await _channels.GetByPublicIdAsync(tenantId, channel.PublicId);
        return (gid, updated!.Id, updated.PublicId);
    }

    private async Task<long> SeedWebhookAsync(long tenantId, long channelId, long userId,
        long discordWebhookId, string token = "fake-token")
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

    private async Task<Message> SeedMessageAsync(long tenantId, long channelId, long snowflake,
        bool viaDwbhub, long? dwbhubUserId = null, bool deleted = false)
    {
        var msg = new Message
        {
            TenantId = tenantId,
            ChannelId = channelId,
            DiscordMessageId = snowflake,
            DiscordAuthorId = dwbhubUserId ?? 99999L,
            DiscordAuthorName = "TestAuthor",
            ViaDwbhub = viaDwbhub,
            DwbhubUserId = viaDwbhub ? dwbhubUserId : null,
            Content = "Test content",
            SentAt = DateTimeOffset.UtcNow,
        };
        var inserted = await _msgRepo.InsertAsync(msg);
        if (deleted)
            await _msgRepo.SoftDeleteByIdAsync(tenantId, inserted!.Id, DateTimeOffset.UtcNow);
        return inserted!;
    }

    private HttpClient BuildClient(string jwt)
    {
        var fake = _discordFake;
        return _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                svc.RemoveAll<IDiscordRestChannelClient>();
                svc.AddSingleton<IDiscordRestChannelClient>(fake);
            }))
            .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    private static string DeleteUrl(string slug, Guid channelPublicId, long messageId) =>
        $"/api/t/{slug}/channels/{channelPublicId:D}/messages/{messageId}";

    // ── 1. Self-delete → 204 with self audit event ───────────────────────────

    [Fact]
    public async Task Delete_self_returns_204_audit_event_is_self()
    {
        var (tid, uid, jwt) = await SeedUserAsync("del-self", "ds@test.local", "Author", UserRole.Owner);
        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000020101L);
        await SeedWebhookAsync(tid, channelId, uid, 900000000000020101L);
        var msg = await SeedMessageAsync(tid, channelId, 800000000000020101L, viaDwbhub: true, dwbhubUserId: uid);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.DeleteAsync(DeleteUrl("del-self", channelPublicId, msg.Id));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Webhook delete must have been called.
        _discordFake.DeleteWebhookCalls.Should().ContainSingle();

        // DB row must be soft-deleted.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var deletedAt = await conn.QuerySingleAsync<DateTimeOffset?>(
            "SELECT deleted_at FROM messages WHERE id = @id", new { id = msg.Id });
        deletedAt.Should().NotBeNull();

        // Audit event is self (not moderation).
        var auditType = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT event_type FROM audit_log WHERE tenant_id = @tid AND event_type LIKE 'message.delete.%' LIMIT 1",
            new { tid });
        auditType.Should().Be("message.delete.self");
    }

    // ── 2. Mod-outbound by Owner → 204 with moderation_outbound audit ────────

    [Fact]
    public async Task Delete_mod_outbound_by_owner_returns_204_audit_event_is_moderation_outbound()
    {
        var (tid, uidOwner, jwtOwner) = await SeedUserAsync("del-modout", "dmo@test.local", "Owner", UserRole.Owner);
        // Create a second user (author) in the same tenant.
        var authorId = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: "author@del-modout.local",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: "Author",
            Role: UserRole.Member, IsActive: true,
            CreatedAt: default, UpdatedAt: default));

        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uidOwner, 100000000000020201L);
        await SeedWebhookAsync(tid, channelId, uidOwner, 900000000000020201L);
        // Message authored by authorId, not uidOwner.
        var msg = await SeedMessageAsync(tid, channelId, 800000000000020201L, viaDwbhub: true, dwbhubUserId: authorId);

        using var client = BuildClient(jwtOwner);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtOwner);

        var res = await client.DeleteAsync(DeleteUrl("del-modout", channelPublicId, msg.Id));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Audit event must be moderation_outbound.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var auditType = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT event_type FROM audit_log WHERE tenant_id = @tid AND event_type LIKE 'message.delete.%' LIMIT 1",
            new { tid });
        auditType.Should().Be("message.delete.moderation_outbound");
    }

    // ── 3. Mod-inbound with bot permission → 204 + moderation_inbound audit ──

    [Fact]
    public async Task Delete_mod_inbound_with_permission_returns_204_audit_event_is_moderation_inbound()
    {
        var (tid, uid, jwt) = await SeedUserAsync("del-modin", "dmi@test.local", "Owner", UserRole.Owner);
        var (guildId, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000020301L);

        // Set bot_can_manage_messages = true on the guild.
        await _guilds.UpdateBotPermissionsAsync(guildId, tid, canManageMessages: true, CancellationToken.None);

        // Inbound message (ViaDwbhub=false, no webhook needed for the inbound msg itself).
        var msg = await SeedMessageAsync(tid, channelId, 800000000000020301L, viaDwbhub: false);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.DeleteAsync(DeleteUrl("del-modin", channelPublicId, msg.Id));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Discord fake must have recorded a DeleteChannel call.
        _discordFake.DeleteChannelCalls.Should().ContainSingle();

        // Audit event must be moderation_inbound.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var auditType = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT event_type FROM audit_log WHERE tenant_id = @tid AND event_type LIKE 'message.delete.%' LIMIT 1",
            new { tid });
        auditType.Should().Be("message.delete.moderation_inbound");
    }

    // ── 4. Mod-inbound without bot permission → 412 with typed body ──────────

    [Fact]
    public async Task Delete_mod_inbound_without_permission_returns_412_with_typed_body()
    {
        var (tid, uid, jwt) = await SeedUserAsync("del-noperm", "dnp@test.local", "Owner", UserRole.Owner);
        var (guildId, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000020401L);

        // bot_can_manage_messages is NULL (not set) — BotMissingPermission expected.
        var msg = await SeedMessageAsync(tid, channelId, 800000000000020401L, viaDwbhub: false);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.DeleteAsync(DeleteUrl("del-noperm", channelPublicId, msg.Id));

        res.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("bot_missing_manage_messages");

        // No delete audit event should exist.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE tenant_id = @tid AND event_type LIKE 'message.delete.%'",
            new { tid });
        count.Should().Be(0);
    }

    // ── 5. Cross-tenant → 404 ────────────────────────────────────────────────

    [Fact]
    public async Task Delete_cross_tenant_returns_404()
    {
        var (_, uidA, jwtA) = await SeedUserAsync("del-xta", "dxa@test.local", "OwnerA", UserRole.Owner);
        var (tidB, uidB, _) = await SeedUserAsync("del-xtb", "dxb@test.local", "OwnerB", UserRole.Owner);
        var (_, channelIdB, channelPublicIdB) = await SeedBridgedSetupAsync(tidB, uidB, 100000000000020501L);
        await SeedWebhookAsync(tidB, channelIdB, uidB, 900000000000020501L);
        var msgB = await SeedMessageAsync(tidB, channelIdB, 800000000000020501L, viaDwbhub: true, dwbhubUserId: uidB);

        using var client = BuildClient(jwtA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtA);

        var res = await client.DeleteAsync(DeleteUrl("del-xta", channelPublicIdB, msgB.Id));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-tenant delete must return 404, not 403, to prevent information disclosure");

        // No audit in either tenant.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE event_type LIKE 'message.delete.%'");
        count.Should().Be(0);
    }

    // ── 6. Already deleted → 410 ─────────────────────────────────────────────

    [Fact]
    public async Task Delete_already_deleted_returns_410()
    {
        var (tid, uid, jwt) = await SeedUserAsync("del-gone", "dg@test.local", "Owner", UserRole.Owner);
        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000020601L);
        await SeedWebhookAsync(tid, channelId, uid, 900000000000020601L);
        var msg = await SeedMessageAsync(tid, channelId, 800000000000020601L, viaDwbhub: true, dwbhubUserId: uid, deleted: true);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.DeleteAsync(DeleteUrl("del-gone", channelPublicId, msg.Id));

        res.StatusCode.Should().Be(HttpStatusCode.Gone);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("already_deleted");
    }

    // ── 7. Non-owner mod attempt → 403 ───────────────────────────────────────

    [Fact]
    public async Task Delete_non_owner_mod_attempt_returns_403()
    {
        // Member user tries to delete another user's outbound message.
        var (tid, uidOwner, _) = await SeedUserAsync("del-forbid", "df-owner@test.local", "Owner", UserRole.Owner);
        var memberUserId = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: "member@del-forbid.local",
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: "Member",
            Role: UserRole.Member, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var memberUser = new User(memberUserId, tid, "member@del-forbid.local", DateTimeOffset.UtcNow,
            "", "Member", UserRole.Member, true, default, default);
        var tenant = new Tenant(tid, "T-del-forbid", "del-forbid", "en", default, default);
        var memberJwt = _issuer.Issue(memberUser, tenant);

        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uidOwner, 100000000000020701L);
        await SeedWebhookAsync(tid, channelId, uidOwner, 900000000000020701L);
        // Message authored by uidOwner.
        var msg = await SeedMessageAsync(tid, channelId, 800000000000020701L, viaDwbhub: true, dwbhubUserId: uidOwner);

        using var client = BuildClient(memberJwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberJwt);

        var res = await client.DeleteAsync(DeleteUrl("del-forbid", channelPublicId, msg.Id));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── 8. Owner deletes own message → self audit (not moderation_outbound) ──

    [Fact]
    public async Task Delete_owner_deletes_own_takes_self_audit_path()
    {
        var (tid, uid, jwt) = await SeedUserAsync("del-ownself", "dos@test.local", "Owner", UserRole.Owner);
        var (_, channelId, channelPublicId) = await SeedBridgedSetupAsync(tid, uid, 100000000000020801L);
        await SeedWebhookAsync(tid, channelId, uid, 900000000000020801L);
        // Owner's own message.
        var msg = await SeedMessageAsync(tid, channelId, 800000000000020801L, viaDwbhub: true, dwbhubUserId: uid);

        using var client = BuildClient(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var res = await client.DeleteAsync(DeleteUrl("del-ownself", channelPublicId, msg.Id));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Audit event must be SELF, not moderation_outbound.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var auditType = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT event_type FROM audit_log WHERE tenant_id = @tid AND event_type LIKE 'message.delete.%' LIMIT 1",
            new { tid });
        auditType.Should().Be("message.delete.self",
            "an Owner deleting their own message follows the self-delete path, not moderation");
    }
}

// ── File-scoped DateTimeOffset handler ───────────────────────────────────────

file sealed class DeleteDateTimeOffsetHandler : Dapper.SqlMapper.TypeHandler<DateTimeOffset>
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
