using DwbHub.Application.Audit;
using DwbHub.Application.Messaging;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DwbHub.Tests.Unit.Messaging;

/// <summary>
/// Unit tests for MessageService. All dependencies are mocked — no DB, no Discord.
/// Covers: inbound filter gates, edit/delete paths, outbound happy path + 404-recreate,
/// and duplicate-snowflake handling.
/// </summary>
public sealed class MessageServiceTests
{
    // ── Shared fixtures ────────────────────────────────────────────────────────

    private static GuildChannel MakeChannel(bool isBridged, short channelType = 0) => new()
    {
        Id = 99,
        TenantId = 1,
        GuildId = 10,
        PublicId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DiscordChannelId = 555L,
        Name = "general",
        ChannelType = channelType,
        Position = 0,
        IsBridged = isBridged,
        LastSyncedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ChannelWebhook MakeWebhook(long webhookId = 777L) => new()
    {
        Id = 1,
        TenantId = 1,
        ChannelId = 99,
        DiscordWebhookId = webhookId,
        Ciphertext = new byte[32],
        Nonce = new byte[12],
        AuthTag = new byte[16],
        KeyVersion = 1,
        CreatedByUserId = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Message MakePersistedMessage(long id = 1001) => new()
    {
        Id = id,
        TenantId = 1,
        ChannelId = 99,
        DiscordMessageId = 12345L,
        DiscordAuthorId = 67890L,
        DiscordAuthorName = "alice",
        ViaDwbhub = false,
        Content = "hello",
        SentAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static MessageReceivedEvent MakeReceivedEvent(
        long discordChannelId = 555L,
        bool authorIsWebhook = false,
        ulong? webhookSrcId = null) => new()
        {
            TenantId = 1,
            GuildId = 10,
            DiscordChannelId = discordChannelId,
            DiscordMessageId = 12345L,
            DiscordAuthorId = 67890L,
            DiscordAuthorName = "alice",
            AuthorIsWebhook = authorIsWebhook,
            WebhookSourceId = webhookSrcId,
            Content = "hello",
            SentAt = DateTimeOffset.UtcNow,
        };

    private (MessageService Svc,
             Mock<IMessageRepository> MsgRepo,
             Mock<IGuildChannelRepository> ChannelRepo,
             Mock<IChannelWebhookRepository> WebhookRepo,
             Mock<IChannelWebhookCipher> Cipher,
             Mock<IDiscordRestChannelClient> Discord,
             Mock<IAuditWriter> Audit,
             Mock<IMessagesBroadcaster> Broadcaster)
        Build()
    {
        var msgRepo = new Mock<IMessageRepository>();
        var channelRepo = new Mock<IGuildChannelRepository>();
        var webhookRepo = new Mock<IChannelWebhookRepository>();
        var cipher = new Mock<IChannelWebhookCipher>();
        var discord = new Mock<IDiscordRestChannelClient>();
        var audit = new Mock<IAuditWriter>();
        var broadcaster = new Mock<IMessagesBroadcaster>();

        // Default: audit always succeeds
        audit.Setup(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1L);

        // Default: broadcaster always succeeds
        broadcaster.Setup(b => b.MessageReceivedAsync(
            It.IsAny<MessageBroadcastDto>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        broadcaster.Setup(b => b.MessageUpdatedAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        broadcaster.Setup(b => b.MessageDeletedAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new MessageService(
            msgRepo.Object, channelRepo.Object, webhookRepo.Object,
            cipher.Object, discord.Object,
            audit.Object, broadcaster.Object,
            NullLogger<MessageService>.Instance);

        return (svc, msgRepo, channelRepo, webhookRepo, cipher, discord, audit, broadcaster);
    }

    // ── PersistInboundAsync: filter tests ─────────────────────────────────────

    [Fact]
    public async Task PersistInbound_ChannelNotFound_ReturnsNull_NoInsert()
    {
        var (svc, msgRepo, channelRepo, _, _, _, audit, broadcaster) = Build();
        channelRepo.Setup(r => r.GetByDiscordIdAsync(1L, 555L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync((GuildChannel?)null);

        var result = await svc.PersistInboundAsync(MakeReceivedEvent());

        result.Should().BeNull();
        msgRepo.Verify(r => r.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Verify(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        broadcaster.Verify(b => b.MessageReceivedAsync(
            It.IsAny<MessageBroadcastDto>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PersistInbound_ChannelNotBridged_ReturnsNull_NoInsert()
    {
        var (svc, msgRepo, channelRepo, _, _, _, audit, broadcaster) = Build();
        channelRepo.Setup(r => r.GetByDiscordIdAsync(1L, 555L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeChannel(isBridged: false));

        var result = await svc.PersistInboundAsync(MakeReceivedEvent());

        result.Should().BeNull();
        msgRepo.Verify(r => r.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PersistInbound_DmChannel_ReturnsNull_NoInsert()
    {
        var (svc, msgRepo, channelRepo, _, _, _, _, _) = Build();
        channelRepo.Setup(r => r.GetByDiscordIdAsync(1L, 555L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeChannel(isBridged: true, channelType: 1)); // DM type

        var result = await svc.PersistInboundAsync(MakeReceivedEvent());

        result.Should().BeNull();
        msgRepo.Verify(r => r.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PersistInbound_WebhookLoop_OurWebhook_ReturnsNull_NoInsert()
    {
        var (svc, msgRepo, channelRepo, webhookRepo, _, _, _, _) = Build();
        channelRepo.Setup(r => r.GetByDiscordIdAsync(1L, 555L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeChannel(isBridged: true));
        webhookRepo.Setup(r => r.GetByChannelAsync(1L, 99L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeWebhook(webhookId: 777L));

        var evt = MakeReceivedEvent(authorIsWebhook: true, webhookSrcId: 777UL);
        var result = await svc.PersistInboundAsync(evt);

        result.Should().BeNull();
        msgRepo.Verify(r => r.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PersistInbound_WebhookFromOtherWebhook_PersistsNormally()
    {
        // A webhook from a DIFFERENT source (not ours) should not be filtered.
        var (svc, msgRepo, channelRepo, webhookRepo, _, _, audit, broadcaster) = Build();
        var channel = MakeChannel(isBridged: true);
        channelRepo.Setup(r => r.GetByDiscordIdAsync(1L, 555L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(channel);
        webhookRepo.Setup(r => r.GetByChannelAsync(1L, 99L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeWebhook(webhookId: 777L)); // our webhook
        var persisted = MakePersistedMessage();
        msgRepo.Setup(r => r.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(persisted);

        var evt = MakeReceivedEvent(authorIsWebhook: true, webhookSrcId: 999UL); // different webhook
        var result = await svc.PersistInboundAsync(evt);

        result.Should().NotBeNull();
        msgRepo.Verify(r => r.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        broadcaster.Verify(b => b.MessageReceivedAsync(
            It.IsAny<MessageBroadcastDto>(), channel.PublicId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistInbound_HappyPath_InsertsAuditsAndBroadcasts()
    {
        var (svc, msgRepo, channelRepo, webhookRepo, _, _, audit, broadcaster) = Build();
        var channel = MakeChannel(isBridged: true);
        channelRepo.Setup(r => r.GetByDiscordIdAsync(1L, 555L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(channel);
        webhookRepo.Setup(r => r.GetByChannelAsync(1L, 99L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync((ChannelWebhook?)null); // no webhook registered
        var persisted = MakePersistedMessage();
        msgRepo.Setup(r => r.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(persisted);

        var result = await svc.PersistInboundAsync(MakeReceivedEvent());

        result.Should().NotBeNull();
        result!.DiscordMessageId.Should().Be(persisted.DiscordMessageId);
        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageReceivedInbound),
            It.IsAny<CancellationToken>()), Times.Once);
        broadcaster.Verify(b => b.MessageReceivedAsync(
            It.IsAny<MessageBroadcastDto>(), channel.PublicId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistInbound_DuplicateSnowflake_InsertReturnsNull_NoAuditNoBroadcast()
    {
        var (svc, msgRepo, channelRepo, webhookRepo, _, _, audit, broadcaster) = Build();
        channelRepo.Setup(r => r.GetByDiscordIdAsync(1L, 555L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeChannel(isBridged: true));
        webhookRepo.Setup(r => r.GetByChannelAsync(1L, 99L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync((ChannelWebhook?)null);
        msgRepo.Setup(r => r.InsertAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((Message?)null); // ON CONFLICT returns null

        var result = await svc.PersistInboundAsync(MakeReceivedEvent());

        result.Should().BeNull();
        audit.Verify(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        broadcaster.Verify(b => b.MessageReceivedAsync(
            It.IsAny<MessageBroadcastDto>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── PersistEditAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task PersistEdit_HappyPath_UpdatesAuditsAndBroadcasts()
    {
        var (svc, msgRepo, _, _, _, _, audit, broadcaster) = Build();
        msgRepo.Setup(r => r.ApplyEditAsync(1L, 12345L, "edited", It.IsAny<DateTimeOffset>(),
                          It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        await svc.PersistEditAsync(new MessageUpdatedEvent
        {
            TenantId = 1,
            GuildId = 10,
            DiscordChannelId = 555L,
            DiscordMessageId = 12345L,
            Content = "edited",
            EditedAt = DateTimeOffset.UtcNow,
        });

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageEdited),
            It.IsAny<CancellationToken>()), Times.Once);
        broadcaster.Verify(b => b.MessageUpdatedAsync(
            1L, 12345L, "edited", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistEdit_RowNotFound_NoAuditNoBroadcast()
    {
        var (svc, msgRepo, _, _, _, _, audit, broadcaster) = Build();
        msgRepo.Setup(r => r.ApplyEditAsync(1L, 12345L, It.IsAny<string>(),
                          It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        await svc.PersistEditAsync(new MessageUpdatedEvent
        {
            TenantId = 1,
            GuildId = 10,
            DiscordChannelId = 555L,
            DiscordMessageId = 12345L,
            Content = "x",
            EditedAt = DateTimeOffset.UtcNow,
        });

        audit.Verify(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        broadcaster.Verify(b => b.MessageUpdatedAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── MarkDeletedAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task MarkDeleted_HappyPath_AuditsAndBroadcasts()
    {
        var (svc, msgRepo, _, _, _, _, audit, broadcaster) = Build();
        msgRepo.Setup(r => r.MarkDeletedAsync(1L, 12345L, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        await svc.MarkDeletedAsync(new MessageDeletedEvent
        {
            TenantId = 1,
            GuildId = 10,
            DiscordChannelId = 555L,
            DiscordMessageId = 12345L,
        });

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageDeleted),
            It.IsAny<CancellationToken>()), Times.Once);
        broadcaster.Verify(b => b.MessageDeletedAsync(1L, 12345L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkDeleted_RowNotFound_NoAuditNoBroadcast()
    {
        var (svc, msgRepo, _, _, _, _, audit, broadcaster) = Build();
        msgRepo.Setup(r => r.MarkDeletedAsync(1L, 12345L, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        await svc.MarkDeletedAsync(new MessageDeletedEvent
        {
            TenantId = 1,
            GuildId = 10,
            DiscordChannelId = 555L,
            DiscordMessageId = 12345L,
        });

        audit.Verify(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        broadcaster.Verify(b => b.MessageDeletedAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── SendOutboundAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task SendOutbound_HappyPath_InsertsWithViaDwbhubAndBroadcasts()
    {
        var (svc, msgRepo, channelRepo, webhookRepo, cipher, discord, audit, broadcaster) = Build();

        var channel = MakeChannel(isBridged: true);
        channelRepo.Setup(r => r.ListBridgedAsync(1L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<GuildChannel> { channel });

        var webhook = MakeWebhook(888L);
        webhookRepo.Setup(r => r.GetByChannelAsync(1L, 99L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(webhook);

        cipher.Setup(c => c.Decrypt(It.IsAny<ChannelWebhookEnvelope>()))
              .Returns("fake-webhook-token");

        var snowflake = 98765UL;
        discord.Setup(d => d.ExecuteWebhookAsync(888UL, "fake-webhook-token", "Alice", "Hello world",
                          It.IsAny<CancellationToken>()))
               .ReturnsAsync(new DiscordMessageInfo(
                   Id: snowflake,
                   AuthorId: 888UL,
                   AuthorName: "Alice",
                   AuthorIsWebhook: true,
                   Content: "Hello world",
                   SentAt: DateTimeOffset.UtcNow,
                   EditedAt: null));

        var persisted = new Message
        {
            Id = 500,
            TenantId = 1,
            ChannelId = 99,
            DiscordMessageId = (long)snowflake,
            DiscordAuthorId = 888L,
            DiscordAuthorName = "Alice",
            ViaDwbhub = true,
            DwbhubUserId = 42L,
            Content = "Hello world",
            SentAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        msgRepo.Setup(r => r.InsertAsync(
            It.Is<Message>(m => m.ViaDwbhub == true && m.DwbhubUserId == 42L),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(persisted);

        var result = await svc.SendOutboundAsync(
            tenantId: 1,
            channelId: 99,
            userId: 42L,
            displayName: "Alice",
            content: "Hello world");

        result.ViaDwbhub.Should().BeTrue();
        result.DwbhubUserId.Should().Be(42L);
        result.DiscordMessageId.Should().Be((long)snowflake);

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageSentOutbound),
            It.IsAny<CancellationToken>()), Times.Once);
        broadcaster.Verify(b => b.MessageReceivedAsync(
            It.IsAny<MessageBroadcastDto>(), channel.PublicId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendOutbound_Discord404_ThrowsInvalidOperation()
    {
        var (svc, _, channelRepo, webhookRepo, cipher, discord, _, _) = Build();

        channelRepo.Setup(r => r.ListBridgedAsync(1L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<GuildChannel> { MakeChannel(isBridged: true) });

        webhookRepo.Setup(r => r.GetByChannelAsync(1L, 99L, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeWebhook(888L));

        cipher.Setup(c => c.Decrypt(It.IsAny<ChannelWebhookEnvelope>()))
              .Returns("fake-token");

        discord.Setup(d => d.ExecuteWebhookAsync(
                    It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new WebhookGoneException(888UL));

        Func<Task> act = () => svc.SendOutboundAsync(1, 99, 42, "Alice", "Hello");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*webhook*not found*");
    }

    // ── ListHistoryAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListHistory_DelegatesToRepository()
    {
        var (svc, msgRepo, _, _, _, _, _, _) = Build();
        var expected = new List<Message> { MakePersistedMessage() };
        msgRepo.Setup(r => r.ListByChannelBeforeAsync(1L, 99L, null, 50, It.IsAny<CancellationToken>()))
               .ReturnsAsync(expected);

        var result = await svc.ListHistoryAsync(1, 99, null, 50);

        result.Should().BeEquivalentTo(expected);
    }
}
