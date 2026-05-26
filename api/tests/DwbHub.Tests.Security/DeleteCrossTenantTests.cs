using System.Data;
using System.Net;
using System.Net.Http.Headers;
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
using DwbHub.Tests.Integration.Messaging;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Security;

/// <summary>
/// Security tests for cross-tenant message delete (Plan 1.1 Task 9).
///
/// Threat model: User authenticated in Tenant A attempts to DELETE a message that
/// belongs to Tenant B. The API must return 404 (not 403) and must not write any
/// audit event in either tenant.
/// </summary>
[Collection(SecurityDatabaseCollection.Name)]
public sealed class DeleteCrossTenantTests : IAsyncLifetime
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

    public DeleteCrossTenantTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DeleteXtDateTimeOffsetHandler());
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
        var uniqueLogDir = Path.Combine(Path.GetTempPath(), "dwbhub-delxt-security-logs", Guid.NewGuid().ToString("N"));
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
        string slug, string email)
    {
        var tid = await _tenants.CreateAsync(name: $"Sec-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: "Owner",
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", "Owner",
            UserRole.Owner, true, default, default);
        var tenant = new Tenant(tid, $"Sec-{slug}", slug, "en", default, default);
        return (tid, uid, _issuer.Issue(user, tenant));
    }

    private async Task<(long channelId, Guid channelPublicId, Message message)> SeedFullSetupAsync(
        long tenantId, long userId, long discordChannelId, long webhookId, long snowflake)
    {
        var (gid, _) = await _guilds.CreateAsync(tenantId, "100000000000040111", "Guild", userId);
        var channel = await _channels.UpsertFromSyncAsync(tenantId, gid, discordChannelId, "general", 0, 0);
        await _channels.SetBridgedAsync(tenantId, channel.PublicId, true);
        var updated = await _channels.GetByPublicIdAsync(tenantId, channel.PublicId);

        var envelope = _cipher.Encrypt("fake-token");
        await _webhooks.InsertAsync(new ChannelWebhook
        {
            TenantId = tenantId,
            ChannelId = updated!.Id,
            DiscordWebhookId = webhookId,
            Ciphertext = envelope.Ciphertext,
            Nonce = envelope.Nonce,
            AuthTag = envelope.AuthTag,
            KeyVersion = envelope.KeyVersion,
            CreatedByUserId = userId,
        });

        var msg = await _msgRepo.InsertAsync(new Message
        {
            TenantId = tenantId,
            ChannelId = updated.Id,
            DiscordMessageId = snowflake,
            DiscordAuthorId = 1L,
            DiscordAuthorName = "Owner",
            ViaDwbhub = true,
            DwbhubUserId = userId,
            Content = "B's message",
            SentAt = DateTimeOffset.UtcNow,
        });

        return (updated.Id, updated.PublicId, msg!);
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

    // ── S1: Cross-tenant delete returns 404 ──────────────────────────────────

    [Fact]
    public async Task User_in_tenant_A_deleting_tenant_B_message_returns_404()
    {
        var (tidA, uidA, jwtA) = await SeedOwnerAsync("del-xt-a", "a@del-xt.local");
        var (tidB, uidB, _) = await SeedOwnerAsync("del-xt-b", "b@del-xt.local");

        var (_, channelBPublicId, msgB) = await SeedFullSetupAsync(
            tidB, uidB, 100000000000040201L, 900000000000040201L, 800000000000040201L);

        using var client = BuildClient(jwtA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtA);

        var url = $"/api/t/del-xt-a/channels/{channelBPublicId:D}/messages/{msgB.Id}";
        var res = await client.DeleteAsync(url);

        // Must be 404, not 403.
        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-tenant delete must return 404 to prevent information disclosure about whether the resource exists");

        // No Discord delete call must have been made.
        _discordFake.DeleteWebhookCalls.Should().BeEmpty();
        _discordFake.DeleteChannelCalls.Should().BeEmpty();

        // Message in Tenant B must remain undeleted.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var deletedAt = await conn.QuerySingleAsync<DateTimeOffset?>(
            "SELECT deleted_at FROM messages WHERE id = @id", new { id = msgB.Id });
        deletedAt.Should().BeNull("cross-tenant delete attempt must not affect Tenant B's message");
    }

    // ── S2: No audit event in either tenant after cross-tenant delete ─────────

    [Fact]
    public async Task User_in_tenant_A_deleting_tenant_B_message_writes_no_audit_event_in_either_tenant()
    {
        var (tidA, uidA, jwtA) = await SeedOwnerAsync("del-xt-audit-a", "aa@del-xt-audit.local");
        var (tidB, uidB, _) = await SeedOwnerAsync("del-xt-audit-b", "ab@del-xt-audit.local");

        var (_, channelBPublicId, msgB) = await SeedFullSetupAsync(
            tidB, uidB, 100000000000040301L, 900000000000040301L, 800000000000040301L);

        using var client = BuildClient(jwtA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwtA);

        var url = $"/api/t/del-xt-audit-a/channels/{channelBPublicId:D}/messages/{msgB.Id}";
        await client.DeleteAsync(url);

        // No audit event for message.delete.* in either tenant.
        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();

        var countInA = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE tenant_id = @tid AND event_type LIKE 'message.delete.%'",
            new { tid = tidA });
        var countInB = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE tenant_id = @tid AND event_type LIKE 'message.delete.%'",
            new { tid = tidB });

        countInA.Should().Be(0, "no delete audit in Tenant A for a rejected cross-tenant request");
        countInB.Should().Be(0, "no delete audit in Tenant B — Tenant B's message was untouched");
    }
}

// ── File-scoped DateTimeOffset handler ───────────────────────────────────────

file sealed class DeleteXtDateTimeOffsetHandler : Dapper.SqlMapper.TypeHandler<DateTimeOffset>
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
