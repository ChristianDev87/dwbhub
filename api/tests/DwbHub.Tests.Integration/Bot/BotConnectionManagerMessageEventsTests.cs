using DwbHub.Application.Audit;
using DwbHub.Application.Bot;
using DwbHub.Application.Messaging;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using DwbHub.Infrastructure.Bot;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DwbHub.Tests.Integration.Bot;

/// <summary>
/// Verifies that <see cref="BotConnectionManager"/> correctly wires the three
/// Discord message events to <see cref="IMessageService"/> when a guild is
/// activated, and that deactivating a guild prevents further dispatch.
///
/// Uses hand-rolled stub collaborators (no Moq, no DB) because the event-wiring
/// logic being tested lives entirely in BotConnectionManager. Full DB coverage
/// for message persistence lives in MessageRepositoryTests + MessageServiceTests.
/// </summary>
public sealed class BotConnectionManagerMessageEventsTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────

    private static Guild FakeGuild(long id, long tenantId, bool isActive = true) =>
        new(Id: id, PublicId: Guid.NewGuid(), TenantId: tenantId,
            DiscordGuildId: "1234567890123456789", DisplayName: $"G{id}",
            IsActive: isActive, RegisteredByUserId: 1,
            RegisteredAt: DateTimeOffset.UtcNow, LastConnectedAt: null,
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    private static GuildBotCredential FakeCred(long guildId, long tenantId) =>
        new(Id: guildId, GuildId: guildId, TenantId: tenantId,
            Nonce: new byte[12],
            Ciphertext: Enumerable.Repeat((byte)0xAA, 80).ToArray(),
            Tag: new byte[16],
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    // Minimal stub IGuildRepository — only the methods BotConnectionManager calls.
    private sealed class StubGuildRepo(long guildId, long tenantId, bool isActive = true) : IGuildRepository
    {
        public Task<Guild?> GetByIdAsync(long id, CancellationToken ct = default)
            => Task.FromResult<Guild?>(id == guildId ? FakeGuild(id, tenantId, isActive) : null);

        public Task<IReadOnlyList<GuildIdTenantPair>> ListActiveWithCredentialsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GuildIdTenantPair>>(new List<GuildIdTenantPair>());

        // UpdateLastConnectedAtAsync is called fire-and-forget in OnStateChangedAsync.
        public Task UpdateLastConnectedAtAsync(long id, DateTimeOffset ts, CancellationToken ct = default)
            => Task.CompletedTask;

        // Remaining methods unused by BotConnectionManager.
        public Task<(long Id, Guid PublicId)> CreateAsync(long tid, string discordGuildId,
            string displayName, long registeredByUserId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<Guild>> ListByTenantAsync(long tid, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<Guild?> GetByPublicIdAsync(Guid publicId, long tid, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> SetActiveAsync(long id, long tid, bool active, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<GuildListItem>> ListByTenantWithStatusAsync(long tid, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid publicId, long tid, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<(Tenant? Tenant, Guild? Guild)> ResolveTenantAndGuildAsync(
            string slug, Guid guildPublicId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateBotPermissionsAsync(long gid, long tid, bool canManageMessages, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubCredRepo(long guildId) : IGuildBotCredentialRepository
    {
        public Task<GuildBotCredential?> GetByGuildIdAsync(long id, long tid, CancellationToken ct = default)
            => Task.FromResult<GuildBotCredential?>(id == guildId ? FakeCred(id, tid) : null);

        public Task<bool> ExistsForGuildAsync(long gid, long tid, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task UpsertAsync(long gid, long tid, CipherEnvelope envelope, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(long gid, long tid, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubEncryptor : DwbHub.Application.Encryption.IBotTokenEncryptor
    {
        public string Decrypt(CipherEnvelope envelope) => "fake-token";
        public CipherEnvelope Encrypt(string plaintext) => throw new NotSupportedException();
    }

    private sealed class StubAuditWriter : IAuditWriter
    {
        public Task<long> RecordAsync(AuditEvent evt, CancellationToken ct = default)
            => Task.FromResult(0L);
    }

    private sealed class StubTenantRepo : ITenantRepository
    {
        public Task<Tenant?> GetByIdAsync(long id, CancellationToken ct = default)
            => Task.FromResult<Tenant?>(null); // null slug is handled gracefully by OnStateChangedAsync
        public Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<long> CreateAsync(string name, string slug, string locale = "de", CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateMessageEditWindowAsync(long tenantId, int? windowSeconds, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // ── Build helper ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="BotConnectionManager"/> backed entirely by stubs.
    /// The <paramref name="svc"/> out-parameter is the concrete recording service
    /// registered as <see cref="IMessageService"/> in the scope factory so tests
    /// can assert on captured calls without going through DI.
    /// </summary>
    private static BotConnectionManager Build(
        long guildId,
        out FakeBotConnectionFactory factory,
        out RecordingMessageService svc,
        long tenantId = 1,
        bool isActive = true)
    {
        factory = new FakeBotConnectionFactory();
        svc = new RecordingMessageService();

        // Capture in local to avoid closure-over-out-param issues.
        var capturedSvc = svc;
        var capturedFactory = factory;

        var sp = new ServiceCollection()
            .AddSingleton<IGuildRepository>(new StubGuildRepo(guildId, tenantId, isActive))
            .AddSingleton<IGuildBotCredentialRepository>(new StubCredRepo(guildId))
            .AddSingleton<DwbHub.Application.Encryption.IBotTokenEncryptor>(new StubEncryptor())
            .AddSingleton<IAuditWriter>(new StubAuditWriter())
            .AddSingleton<ITenantRepository>(new StubTenantRepo())
            .AddSingleton<IMessageService>(capturedSvc)
            .BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new BotConnectionManager(capturedFactory, scopeFactory,
            NullLogger<BotConnectionManager>.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ActivateGuild_RaisesMessageReceived_PersistInboundIsInvoked()
    {
        var mgr = Build(10, out var factory, out var svc);

        await mgr.OnGuildActivatedAsync(10, CancellationToken.None);
        factory.Created[10].State.Should().Be(BotConnectionState.Connected);

        var evt = new MessageReceivedEvent
        {
            TenantId = 1,
            GuildId = 10,
            DiscordChannelId = 500L,
            DiscordMessageId = 9876543210L,
            DiscordAuthorId = 111L,
            DiscordAuthorName = "alice",
            Content = "hello",
            SentAt = DateTimeOffset.UtcNow,
        };

        await factory.Created[10].RaiseMessageReceivedAsync(evt);

        svc.ReceivedEvents.Should().ContainSingle(
            e => e.DiscordMessageId == 9876543210L,
            "handler must forward MessageReceived to IMessageService.PersistInboundAsync");
    }

    [Fact]
    public async Task ActivateGuild_RaisesMessageUpdatedAndDeleted_EditAndDeleteAreInvoked()
    {
        var mgr = Build(20, out var factory, out var svc);

        await mgr.OnGuildActivatedAsync(20, CancellationToken.None);
        var fake = factory.Created[20];

        var updEvt = new MessageUpdatedEvent
        {
            TenantId = 1,
            GuildId = 20,
            DiscordChannelId = 500L,
            DiscordMessageId = 111111L,
            Content = "edited",
            EditedAt = DateTimeOffset.UtcNow,
        };
        var delEvt = new MessageDeletedEvent
        {
            TenantId = 1,
            GuildId = 20,
            DiscordChannelId = 500L,
            DiscordMessageId = 222222L,
        };

        await fake.RaiseMessageUpdatedAsync(updEvt);
        await fake.RaiseMessageDeletedAsync(delEvt);

        svc.UpdatedEvents.Should().ContainSingle(
            e => e.DiscordMessageId == 111111L,
            "PersistEditAsync must be invoked on MessageUpdated");
        svc.DeletedEvents.Should().ContainSingle(
            e => e.DiscordMessageId == 222222L,
            "MarkDeletedAsync must be invoked on MessageDeleted");
    }

    [Fact]
    public async Task DeactivateAndReactivate_FreshConnectionHasFreshHandler_TotalCountCorrect()
    {
        // Activate → raise 1 event → deactivate → re-activate → raise another event.
        // ReceivedEvents count must reach 2 (one from each connection).

        var mgr = Build(30, out var factory, out var svc);

        await mgr.OnGuildActivatedAsync(30, CancellationToken.None);
        var firstConn = factory.Created[30];

        await firstConn.RaiseMessageReceivedAsync(new MessageReceivedEvent
        {
            TenantId = 1,
            GuildId = 30,
            DiscordChannelId = 500L,
            DiscordMessageId = 1001L,
            DiscordAuthorId = 1L,
            DiscordAuthorName = "alice",
            Content = "first",
            SentAt = DateTimeOffset.UtcNow,
        });
        svc.ReceivedEvents.Should().HaveCount(1, "first raise before deactivate");

        // Deactivate — connection removed from manager.
        await mgr.OnGuildDeactivatedAsync(30, CancellationToken.None);
        mgr.GetState(30).Should().BeNull("removed from map on deactivate");

        // Re-activate: brand-new connection + brand-new handlers.
        await mgr.OnGuildActivatedAsync(30, CancellationToken.None);
        var secondConn = factory.Created[30];
        secondConn.Should().NotBeSameAs(firstConn, "fresh instance on re-activate");

        await secondConn.RaiseMessageReceivedAsync(new MessageReceivedEvent
        {
            TenantId = 1,
            GuildId = 30,
            DiscordChannelId = 500L,
            DiscordMessageId = 1002L,
            DiscordAuthorId = 1L,
            DiscordAuthorName = "alice",
            Content = "second",
            SentAt = DateTimeOffset.UtcNow,
        });
        svc.ReceivedEvents.Should().HaveCount(2, "second raise from re-activated connection");
        svc.ReceivedEvents
            .Select(e => e.DiscordMessageId)
            .Should()
            .ContainInOrder(new[] { 1001L, 1002L }, "events arrive in order");
    }
}

/// <summary>
/// Test-only IMessageService that records every invocation.
/// No DB — captures calls for assertion only.
/// </summary>
internal sealed class RecordingMessageService : IMessageService
{
    public List<MessageReceivedEvent> ReceivedEvents { get; } = [];
    public List<MessageUpdatedEvent> UpdatedEvents { get; } = [];
    public List<MessageDeletedEvent> DeletedEvents { get; } = [];

    public Task<Message?> PersistInboundAsync(MessageReceivedEvent evt, CancellationToken ct = default)
    {
        ReceivedEvents.Add(evt);
        return Task.FromResult<Message?>(null);
    }

    public Task PersistEditAsync(MessageUpdatedEvent evt, CancellationToken ct = default)
    {
        UpdatedEvents.Add(evt);
        return Task.CompletedTask;
    }

    public Task MarkDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default)
    {
        DeletedEvents.Add(evt);
        return Task.CompletedTask;
    }

    public Task<Message> SendOutboundAsync(long tenantId, long channelId, long userId,
        string displayName, string content, CancellationToken ct = default)
        => throw new NotSupportedException("Not used in these tests.");

    public Task<IReadOnlyList<Message>> ListHistoryAsync(long tenantId, long channelId,
        long? beforeSnowflake, int limit, CancellationToken ct = default)
        => throw new NotSupportedException("Not used in these tests.");
}
