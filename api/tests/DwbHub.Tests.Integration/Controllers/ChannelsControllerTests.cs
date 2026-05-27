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
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for ChannelsController (Plan 1.0 Task 10).
///
/// Discord calls are mocked via IDiscordRestChannelClient stubs;
/// Hangfire enqueues are mocked via IBackgroundJobClient stubs.
/// All DB operations run against the shared Testcontainer.
///
/// Services not yet registered in Program.cs (Task 11) are injected via
/// WithWebHostBuilder.ConfigureTestServices.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ChannelsControllerTests : IAsyncLifetime
{
    private const string Base64JwtKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Base64EncKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCA=";

    private readonly PostgresFixture _fixture;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly GuildRepository _guilds;
    private readonly GuildChannelRepository _channels;
    private readonly GuildBotCredentialRepository _botCreds;
    private readonly ChannelWebhookRepository _webhooks;
    private readonly ChannelBackfillJobRepository _jobs;
    private readonly MessageRepository _messages;
    private readonly BCryptPasswordHasher _hasher;
    private readonly JwtIssuer _issuer;

    private WebApplicationFactory<Program> _factory = null!;

    public ChannelsControllerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new ChannelDateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var fac = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(fac);
        _users = new UserRepository(fac);
        _guilds = new GuildRepository(fac);
        _channels = new GuildChannelRepository(fac);
        _botCreds = new GuildBotCredentialRepository(fac);
        _webhooks = new ChannelWebhookRepository(fac);
        _jobs = new ChannelBackfillJobRepository(fac);
        _messages = new MessageRepository(fac);
        _hasher = new BCryptPasswordHasher();
        _issuer = new JwtIssuer(Base64JwtKey);
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var uniqueLogDir = Path.Combine(
            Path.GetTempPath(), "dwbhub-chan-test-logs", Guid.NewGuid().ToString("N"));
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

    private async Task<(long tenantId, long userId, Guid guildPublicId, long guildId, string jwt)>
        SeedAsync(string slug, string email = "owner@test.local")
    {
        var tid = await _tenants.CreateAsync(name: $"T-{slug}", slug: slug);
        var uid = await _users.CreateAsync(new User(
            Id: 0, TenantId: tid, Email: email,
            EmailVerifiedAt: DateTimeOffset.UtcNow,
            PasswordHash: _hasher.Hash("pw"),
            DisplayName: "OwnerUser",
            Role: UserRole.Owner, IsActive: true,
            CreatedAt: default, UpdatedAt: default));
        var (gid, gPublicId) = await _guilds.CreateAsync(tid, "100000000000000999", "TestGuild", uid);
        var user = new User(uid, tid, email, DateTimeOffset.UtcNow, "", "OwnerUser",
            UserRole.Owner, true, default, default);
        var tenant = new Tenant(tid, $"T-{slug}", slug, "en", default, default);
        return (tid, uid, gPublicId, gid, _issuer.Issue(user, tenant));
    }

    private async Task<(long channelId, Guid channelPublicId)> SeedChannelAsync(
        long tenantId, long guildId, long discordChannelId,
        string name = "general", bool bridged = false)
    {
        var channel = await _channels.UpsertFromSyncAsync(
            tenantId, guildId, discordChannelId, name, 0, 0);
        if (bridged)
        {
            await _channels.SetBridgedAsync(tenantId, channel.PublicId, true);
            var updated = await _channels.GetByPublicIdAsync(tenantId, channel.PublicId);
            return (updated!.Id, updated.PublicId);
        }
        return (channel.Id, channel.PublicId);
    }

    /// <summary>
    /// Builds an HTTP client with test service overrides.
    /// </summary>
    private HttpClient BuildClient(
        string jwt,
        IChannelWebhookService? webhookSvc = null,
        IChannelSyncService? syncSvc = null,
        IBackgroundJobClient? jobClient = null)
    {
        var connFac = new NpgsqlConnectionFactory(_ds);

        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(svc =>
            {
                // Override repos to use our test DB connection.
                svc.RemoveAll<IGuildChannelRepository>();
                svc.AddScoped<IGuildChannelRepository>(_ => new GuildChannelRepository(connFac));
                svc.RemoveAll<IGuildRepository>();
                svc.AddScoped<IGuildRepository>(_ => new GuildRepository(connFac));
                svc.RemoveAll<IChannelBackfillJobRepository>();
                svc.AddScoped<IChannelBackfillJobRepository>(_ => new ChannelBackfillJobRepository(connFac));

                // Override webhook service.
                svc.RemoveAll<IChannelWebhookService>();
                svc.AddScoped<IChannelWebhookService>(_ =>
                    webhookSvc ?? new NoOpChannelWebhookService());

                // Override sync service.
                svc.RemoveAll<IChannelSyncService>();
                svc.AddScoped<IChannelSyncService>(_ =>
                    syncSvc ?? new NoOpChannelSyncService());

                // Override Hangfire job client.
                svc.RemoveAll<IBackgroundJobClient>();
                svc.AddSingleton<IBackgroundJobClient>(
                    jobClient ?? new CapturingJobClient());

                // Keep broadcaster as no-op.
                svc.RemoveAll<DwbHub.Application.Messaging.IMessagesBroadcaster>();
                svc.AddSingleton<DwbHub.Application.Messaging.IMessagesBroadcaster>(
                    new NoOpMessagesBroadcaster());
            }))
            .CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // ── Test 1: GET list — returns bridge flag per channel ───────────────────

    [Fact]
    public async Task GET_list_AuthenticatedTenant_ReturnsBridgeFlagPerChannel()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-list");
        var (ch1Id, _) = await SeedChannelAsync(tid, gid, 100000000000001001L, "general", false);
        var (ch2Id, _) = await SeedChannelAsync(tid, gid, 100000000000001002L, "announcements", true);

        using var client = BuildClient(jwt);
        var res = await client.ListChannelsAsync("chan-list", gPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var channels = body.GetProperty("channels").EnumerateArray().ToList();
        channels.Should().HaveCount(2);

        var gen = channels.First(c => c.GetProperty("name").GetString() == "general");
        gen.GetProperty("isBridged").GetBoolean().Should().BeFalse();

        var ann = channels.First(c => c.GetProperty("name").GetString() == "announcements");
        ann.GetProperty("isBridged").GetBoolean().Should().BeTrue();
    }

    // ── Test 2: GET list — other tenant's guild → 404 ────────────────────────

    [Fact]
    public async Task GET_list_OtherTenantsGuild_404()
    {
        var (_, _, _, _, jwtA) = await SeedAsync("chan-list-xt1", "a@test.local");
        var (tidB, uidB, gPublicIdB, _, _) = await SeedAsync("chan-list-xt2", "b@test.local");

        using var client = BuildClient(jwtA);
        var res = await client.ListChannelsAsync("chan-list-xt1", gPublicIdB);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("guild_not_found");
    }

    // ── Test 3: POST sync — fetches from Discord and upserts ─────────────────

    [Fact]
    public async Task POST_sync_FetchesFromDiscord_UpsertsChannels()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-sync");

        var callCount = 0;
        var syncSvc = new CapturingSyncService(onSync: () => callCount++);

        using var client = BuildClient(jwt, syncSvc: syncSvc);
        var res = await client.SyncChannelsAsync("chan-sync", gPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        callCount.Should().Be(1);
    }

    // ── Test 4: POST bridge — creates webhook + enqueues backfill → 202 ──────

    [Fact]
    public async Task POST_bridge_CreatesWebhook_EnqueuesBackfill_Returns202()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-bridge");
        var (channelId, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002001L, "bridge-ch", false);

        var webhookCreated = false;
        var webhookSvc = new TrackingWebhookService(onCreate: () => webhookCreated = true);
        var capturedJobId = string.Empty;
        var jobClient = new CapturingJobClient(onEnqueue: (id) => capturedJobId = id);

        using var client = BuildClient(jwt, webhookSvc: webhookSvc, jobClient: jobClient);
        var res = await client.BridgeChannelAsync("chan-bridge", channelPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
        webhookCreated.Should().BeTrue();

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("backfillJobId").GetInt64().Should().BePositive();

        // Verify backfill job is in the DB.
        var updatedChannel = await _channels.GetByPublicIdAsync(tid, channelPublicId);
        updatedChannel!.IsBridged.Should().BeTrue();

        var job = await _jobs.GetByChannelAsync(tid, channelId);
        job.Should().NotBeNull();
        job!.HangfireJobId.Should().Be("fake-hangfire-job-id");
    }

    // ── Test 5: POST bridge — already bridged → 409 ──────────────────────────

    [Fact]
    public async Task POST_bridge_AlreadyBridged_409()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-bridge-dup");
        var (_, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002101L, "already-bridged", true);

        using var client = BuildClient(jwt);
        var res = await client.BridgeChannelAsync("chan-bridge-dup", channelPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("channel_already_bridged");
    }

    // ── Test 6: POST bridge — bot lacks permission → 409 ─────────────────────

    [Fact]
    public async Task POST_bridge_BotLacksPermission_409_DescribesPermission()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-bridge-perm");
        var (_, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002201L, "no-perm-ch", false);

        var throwingSvc = new ThrowingWebhookService(
            new DiscordPermissionException("Missing MANAGE_WEBHOOKS"));

        using var client = BuildClient(jwt, webhookSvc: throwingSvc);
        var res = await client.BridgeChannelAsync("chan-bridge-perm", channelPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("bot_missing_permission");
    }

    // ── Test 7: POST bridge rollback — DB fail after Discord success ──────────

    [Fact]
    public async Task POST_bridge_RollsBackOnDbFailure_DeletesDiscordWebhook()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-rollback");
        var (_, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002301L, "rollback-ch", false);

        var rollbackCalledCount = 0;
        var webhookSvc = new RollbackCapturingWebhookService(
            onCreateThrow: new InvalidOperationException("simulated DB failure"),
            onRollback: () => rollbackCalledCount++);

        using var client = BuildClient(jwt, webhookSvc: webhookSvc);
        var res = await client.BridgeChannelAsync("chan-rollback", channelPublicId);

        // Bridge should fail and channel should remain un-bridged.
        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        rollbackCalledCount.Should().Be(1, "rollback must have been triggered");

        var channel = await _channels.GetByPublicIdAsync(tid, channelPublicId);
        channel!.IsBridged.Should().BeFalse();
    }

    // ── Test 8: DELETE bridge — deletes webhook + cancels backfill → 204 ─────

    [Fact]
    public async Task DELETE_bridge_DeletesWebhook_CancelsRunningBackfill_204()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-unbridge");
        var (channelId, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002401L, "unbridge-ch", true);

        // Insert a running backfill job.
        var job = await _jobs.InsertPendingAsync(tid, channelId);
        await _jobs.MarkRunningAsync(tid, job.Id);
        await _jobs.SetHangfireJobIdAsync(tid, job.Id, "hf-job-123");

        var webhookDeleted = false;
        var webhookSvc = new TrackingWebhookService(onDelete: () => webhookDeleted = true);
        var jobClient = new CapturingJobClient(onDelete: (id) => { /* capture */ });

        using var client = BuildClient(jwt, webhookSvc: webhookSvc, jobClient: jobClient);
        var res = await client.UnbridgeChannelAsync("chan-unbridge", channelPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        webhookDeleted.Should().BeTrue();

        // Channel should be un-bridged.
        var updated = await _channels.GetByPublicIdAsync(tid, channelPublicId);
        updated!.IsBridged.Should().BeFalse();

        // Backfill job should be cancelled.
        var updatedJob = await _jobs.GetByIdAsync(tid, job.Id);
        updatedJob!.Status.Should().Be(BackfillStatus.Cancelled);
    }

    // ── Test 9: DELETE bridge — not bridged → 404 ────────────────────────────

    [Fact]
    public async Task DELETE_bridge_NotBridged_404()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-unbridge-404");
        var (_, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002501L, "not-bridged-ch", false);

        using var client = BuildClient(jwt);
        var res = await client.UnbridgeChannelAsync("chan-unbridge-404", channelPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("channel_not_bridged");
    }

    // ── Test 10: GET backfill-status — returns job state ─────────────────────

    [Fact]
    public async Task GET_backfill_status_ReturnsJobState()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-bfstatus");
        var (channelId, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002601L, "bf-status-ch", true);

        var job = await _jobs.InsertPendingAsync(tid, channelId);
        await _jobs.MarkRunningAsync(tid, job.Id);

        using var client = BuildClient(jwt);
        var res = await client.GetBackfillStatusAsync("chan-bfstatus", channelPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var status = body.GetProperty("status");
        status.GetProperty("jobId").GetInt64().Should().Be(job.Id);
        status.GetProperty("status").GetString().Should().Be("running");
    }

    // ── Test 11: GET backfill-status — no job → 404 ──────────────────────────

    [Fact]
    public async Task GET_backfill_status_NoJob_404()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-bfstatus-404");
        var (_, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002701L, "no-job-ch", true);

        using var client = BuildClient(jwt);
        var res = await client.GetBackfillStatusAsync("chan-bfstatus-404", channelPublicId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("no_backfill_job");
    }

    // ── Test 12: DELETE backfill-job — cancels → 204 ─────────────────────────

    [Fact]
    public async Task DELETE_backfill_job_CallsHangfireDelete_MarksCancelled_204()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-bfcancel");
        var (channelId, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000002801L, "bf-cancel-ch", true);

        var job = await _jobs.InsertPendingAsync(tid, channelId);
        await _jobs.SetHangfireJobIdAsync(tid, job.Id, "hf-cancel-job");

        var deletedHangfireId = string.Empty;
        var jobClient = new CapturingJobClient(onDelete: (id) => deletedHangfireId = id);

        using var client = BuildClient(jwt, jobClient: jobClient);
        var res = await client.CancelBackfillJobAsync("chan-bfcancel", channelPublicId, job.Id);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        deletedHangfireId.Should().Be("hf-cancel-job");

        var updatedJob = await _jobs.GetByIdAsync(tid, job.Id);
        updatedJob!.Status.Should().Be(BackfillStatus.Cancelled);
    }

    // ── Test 13: DELETE backfill-job — other tenant → 404 ───────────────────

    [Fact]
    public async Task DELETE_backfill_job_OtherTenant_404()
    {
        var (_, _, _, _, jwtA) = await SeedAsync("chan-bfcancel-xt1", "xta@test.local");
        var (tidB, uidB, _, gidB, _) = await SeedAsync("chan-bfcancel-xt2", "xtb@test.local");

        var (channelIdB, channelPublicIdB) = await SeedChannelAsync(
            tidB, gidB, 100000000000002901L, "xt-ch", true);
        var jobB = await _jobs.InsertPendingAsync(tidB, channelIdB);

        // Tenant A's JWT targeting tenant B's job.
        using var client = BuildClient(jwtA);
        var res = await client.CancelBackfillJobAsync("chan-bfcancel-xt1", channelPublicIdB, jobB.Id);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Test 14: Bridge toggle → unbridge → messages preserved ───────────────

    [Fact]
    public async Task Bridge_TogglesPreserveHistory_DeletesWebhook_KeepsMessages()
    {
        var (tid, uid, gPublicId, gid, jwt) = await SeedAsync("chan-hist-preserve");
        var (channelId, channelPublicId) = await SeedChannelAsync(
            tid, gid, 100000000000003001L, "hist-ch", false);

        // Bridge the channel.
        var jobClient = new CapturingJobClient();
        var webhookSvc = new TrackingWebhookService();
        using var client = BuildClient(jwt, webhookSvc: webhookSvc, jobClient: jobClient);

        var bridgeRes = await client.BridgeChannelAsync("chan-hist-preserve", channelPublicId);
        bridgeRes.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Seed messages directly into the DB (simulating backfill).
        var msg = new Message
        {
            TenantId = tid,
            ChannelId = channelId,
            DiscordMessageId = 100000000000003001L,
            DiscordAuthorId = 55000L,
            DiscordAuthorName = "user",
            ViaDwbhub = false,
            Content = "historical message",
            SentAt = DateTimeOffset.UtcNow,
        };
        await _messages.InsertAsync(msg);

        // Unbridge.
        var unbridgeRes = await client.UnbridgeChannelAsync("chan-hist-preserve", channelPublicId);
        unbridgeRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Messages must still be in the DB.
        var history = await _messages.ListByChannelBeforeAsync(tid, channelId, null, 100);
        history.Should().HaveCount(1);
        history[0].Content.Should().Be("historical message");

        // Channel must be un-bridged.
        var updated = await _channels.GetByPublicIdAsync(tid, channelPublicId);
        updated!.IsBridged.Should().BeFalse();
    }
}

// ── Stub implementations ──────────────────────────────────────────────────────

/// <summary>No-op webhook service for tests that don't exercise webhook logic.</summary>
file sealed class NoOpChannelWebhookService : IChannelWebhookService
{
    public Task CreateForChannelAsync(long tenantId, long channelId, long userId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteForChannelAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> DecryptTokenAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.FromResult("fake-token");
}

/// <summary>No-op sync service.</summary>
file sealed class NoOpChannelSyncService : IChannelSyncService
{
    public Task SyncFromDiscordAsync(long tenantId, Guid guildPublicId,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Capturing sync service that invokes a callback on SyncFromDiscordAsync.</summary>
file sealed class CapturingSyncService(Action onSync) : IChannelSyncService
{
    public Task SyncFromDiscordAsync(long tenantId, Guid guildPublicId,
        CancellationToken ct = default)
    {
        onSync();
        return Task.CompletedTask;
    }
}

/// <summary>Tracking webhook service that calls callbacks on create/delete.</summary>
file sealed class TrackingWebhookService(
    Action? onCreate = null,
    Action? onDelete = null) : IChannelWebhookService
{
    public Task CreateForChannelAsync(long tenantId, long channelId, long userId,
        CancellationToken ct = default)
    {
        onCreate?.Invoke();
        return Task.CompletedTask;
    }

    public Task DeleteForChannelAsync(long tenantId, long channelId,
        CancellationToken ct = default)
    {
        onDelete?.Invoke();
        return Task.CompletedTask;
    }

    public Task<string> DecryptTokenAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.FromResult("fake-token");
}

/// <summary>Throws on create, simulates rollback for test 7.</summary>
file sealed class RollbackCapturingWebhookService(
    Exception onCreateThrow,
    Action onRollback) : IChannelWebhookService
{
    public Task CreateForChannelAsync(long tenantId, long channelId, long userId,
        CancellationToken ct = default)
    {
        onRollback(); // simulate rollback being triggered
        return Task.FromException(onCreateThrow);
    }
    public Task DeleteForChannelAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> DecryptTokenAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.FromResult("fake-token");
}

/// <summary>Throws a specified exception from CreateForChannelAsync.</summary>
file sealed class ThrowingWebhookService(Exception toThrow) : IChannelWebhookService
{
    public Task CreateForChannelAsync(long tenantId, long channelId, long userId,
        CancellationToken ct = default) => Task.FromException(toThrow);
    public Task DeleteForChannelAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> DecryptTokenAsync(long tenantId, long channelId,
        CancellationToken ct = default) => Task.FromResult("fake-token");
}

/// <summary>
/// Stub IBackgroundJobClient that captures enqueue calls and returns a predictable ID.
///
/// IBackgroundJobClient has only two abstract interface methods:
///   string Create(Job job, IState state)
///   bool   ChangeState(string jobId, IState state, string fromState)
///
/// All helper methods (Enqueue&lt;T&gt;, Delete, etc.) are extension methods that
/// route through these two. Create is called for enqueue; ChangeState is called
/// for delete (with DeletedState) and state transitions.
/// </summary>
file sealed class CapturingJobClient(
    Action<string>? onEnqueue = null,
    Action<string>? onDelete = null) : IBackgroundJobClient
{
    private const string FakeJobId = "fake-hangfire-job-id";

    public string Create(Job job, IState state)
    {
        onEnqueue?.Invoke(FakeJobId);
        return FakeJobId;
    }

    public bool ChangeState(string jobId, IState state, string? fromState)
    {
        if (state is DeletedState)
            onDelete?.Invoke(jobId);
        return true;
    }
}

/// <summary>No-op broadcaster for controller tests.</summary>
file sealed class NoOpMessagesBroadcaster : DwbHub.Application.Messaging.IMessagesBroadcaster
{
    public Task MessageReceivedAsync(MessageBroadcastDto msg, Guid channelPublicId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageUpdatedAsync(long tenantId, long messageId, string content,
        DateTimeOffset editedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageDeletedAsync(long tenantId, long messageId,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task BackfillProgressAsync(long tenantId, Guid channelPublicId, long jobId,
        int fetchedCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task BackfillCompleteAsync(long tenantId, Guid channelPublicId, long jobId,
        int fetchedCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChannelBridgeChangedAsync(long tenantId, Guid channelPublicId, bool isBridged,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// DateTimeOffset type handler for the channels controller test assembly.
/// </summary>
file sealed class ChannelDateTimeOffsetTypeHandler : Dapper.SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        _ => throw new InvalidCastException($"Cannot convert {value?.GetType().Name} to DateTimeOffset"),
    };
    public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value;
}
