using DwbHub.Application.Audit;
using DwbHub.Application.Messaging;
using DwbHub.Core.Entities;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DwbHub.Tests.Unit.Messaging;

/// <summary>
/// Unit tests for <see cref="MessageService.EditAsync"/>.
/// All dependencies are mocked — no DB, no Discord.
/// </summary>
public sealed class MessageServiceEditTests
{
    private const long TenantId = 1L;
    private const long ActorUserId = 42L;
    private const long MessageId = 1001L;
    private const long ChannelId = 99L;
    private const long DiscordMessageId = 12345L;
    private const long WebhookId = 777L;

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static Message MakeOutboundMessage(
        long? deletedAt_offsetSeconds = null,
        string content = "hello world",
        DateTimeOffset? sentAt = null) => new()
        {
            Id = MessageId,
            TenantId = TenantId,
            ChannelId = ChannelId,
            DiscordMessageId = DiscordMessageId,
            DiscordAuthorId = 99L,
            DiscordAuthorName = "alice",
            ViaDwbhub = true,
            DwbhubUserId = ActorUserId,
            Content = content,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
            DeletedAt = deletedAt_offsetSeconds.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(-deletedAt_offsetSeconds.Value)
            : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static Message MakeInboundMessage() => new()
    {
        Id = MessageId,
        TenantId = TenantId,
        ChannelId = ChannelId,
        DiscordMessageId = DiscordMessageId,
        DiscordAuthorId = 99L,
        DiscordAuthorName = "discord-user",
        ViaDwbhub = false,
        DwbhubUserId = null,
        Content = "inbound message",
        SentAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Message MakeOtherUsersOutboundMessage() => new()
    {
        Id = MessageId,
        TenantId = TenantId,
        ChannelId = ChannelId,
        DiscordMessageId = DiscordMessageId,
        DiscordAuthorId = 99L,
        DiscordAuthorName = "bob",
        ViaDwbhub = true,
        DwbhubUserId = ActorUserId + 1, // different user
        Content = "bob's message",
        SentAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ChannelWebhook MakeWebhook() => new()
    {
        Id = 1,
        TenantId = TenantId,
        ChannelId = ChannelId,
        DiscordWebhookId = WebhookId,
        Ciphertext = new byte[32],
        Nonce = new byte[12],
        AuthTag = new byte[16],
        KeyVersion = 1,
        CreatedByUserId = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Tenant MakeTenant(int? editWindowSeconds = null) => new(
        Id: TenantId,
        Name: "Test Tenant",
        Slug: "test",
        Locale: "en",
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        MessageEditWindowSeconds: editWindowSeconds);

    // ── Build helper ───────────────────────────────────────────────────────────

    private (MessageService Svc,
             Mock<IMessageRepository> MsgRepo,
             Mock<IGuildChannelRepository> ChannelRepo,
             Mock<IChannelWebhookRepository> WebhookRepo,
             Mock<IChannelWebhookCipher> Cipher,
             Mock<IDiscordRestChannelClient> Discord,
             Mock<IAuditWriter> Audit,
             Mock<IMessagesBroadcaster> Broadcaster,
             Mock<ITenantRepository> Tenants,
             Mock<IGuildRepository> Guilds)
        Build()
    {
        var msgRepo = new Mock<IMessageRepository>();
        var channelRepo = new Mock<IGuildChannelRepository>();
        var webhookRepo = new Mock<IChannelWebhookRepository>();
        var cipher = new Mock<IChannelWebhookCipher>();
        var discord = new Mock<IDiscordRestChannelClient>();
        var audit = new Mock<IAuditWriter>();
        var broadcaster = new Mock<IMessagesBroadcaster>();
        var tenants = new Mock<ITenantRepository>();
        var guilds = new Mock<IGuildRepository>();

        audit.Setup(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1L);

        broadcaster.Setup(b => b.MessageReceivedAsync(
            It.IsAny<MessageBroadcastDto>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        broadcaster.Setup(b => b.MessageUpdatedAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        broadcaster.Setup(b => b.MessageDeletedAsync(
            It.IsAny<MessageDeletedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        cipher.Setup(c => c.Decrypt(It.IsAny<ChannelWebhookEnvelope>()))
              .Returns("fake-webhook-token");

        // Default: no edit window constraint
        tenants.Setup(t => t.GetByIdAsync(TenantId, It.IsAny<CancellationToken>()))
               .ReturnsAsync((Tenant?)null);

        var svc = new MessageService(
            msgRepo.Object, channelRepo.Object, webhookRepo.Object,
            cipher.Object, discord.Object,
            audit.Object, broadcaster.Object,
            NullLogger<MessageService>.Instance,
            tenants.Object, guilds.Object);

        return (svc, msgRepo, channelRepo, webhookRepo, cipher, discord, audit, broadcaster, tenants, guilds);
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EditAsync_empty_content_returns_EmptyContent()
    {
        var (svc, _, _, _, _, _, _, _, _, _) = Build();

        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, "   ");

        result.Should().BeOfType<EditMessageOutcome.EmptyContent>();
    }

    [Fact]
    public async Task EditAsync_too_long_returns_ContentTooLong()
    {
        var (svc, _, _, _, _, _, _, _, _, _) = Build();
        var tooLong = new string('x', 2001);

        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, tooLong);

        var outcome = result.Should().BeOfType<EditMessageOutcome.ContentTooLong>().Subject;
        outcome.Length.Should().Be(2001);
        outcome.Max.Should().Be(2000);
    }

    [Fact]
    public async Task EditAsync_unknown_message_returns_NotFound()
    {
        var (svc, msgRepo, _, _, _, _, _, _, _, _) = Build();
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync((Message?)null);

        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, "new content");

        result.Should().BeOfType<EditMessageOutcome.NotFound>();
    }

    [Fact]
    public async Task EditAsync_deleted_message_returns_AlreadyDeleted()
    {
        var (svc, msgRepo, _, _, _, _, _, _, _, _) = Build();
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(deletedAt_offsetSeconds: 10));

        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, "new content");

        result.Should().BeOfType<EditMessageOutcome.AlreadyDeleted>();
    }

    [Fact]
    public async Task EditAsync_someone_elses_message_returns_Forbidden()
    {
        var (svc, msgRepo, _, _, _, _, _, _, _, _) = Build();
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOtherUsersOutboundMessage());

        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, "new content");

        result.Should().BeOfType<EditMessageOutcome.Forbidden>();
    }

    [Fact]
    public async Task EditAsync_inbound_message_returns_Forbidden()
    {
        var (svc, msgRepo, _, _, _, _, _, _, _, _) = Build();
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeInboundMessage());

        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, "new content");

        result.Should().BeOfType<EditMessageOutcome.Forbidden>();
    }

    [Fact]
    public async Task EditAsync_outside_edit_window_returns_EditWindowExpired()
    {
        var (svc, msgRepo, _, _, _, _, _, _, tenants, _) = Build();

        // Message was sent 120 seconds ago; window is 60 seconds.
        var fixedNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        svc._now = () => fixedNow;

        var sentAt = fixedNow.AddSeconds(-120);
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(sentAt: sentAt));

        tenants.Setup(t => t.GetByIdAsync(TenantId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeTenant(editWindowSeconds: 60));

        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, "new content");

        var outcome = result.Should().BeOfType<EditMessageOutcome.EditWindowExpired>().Subject;
        outcome.AgeSeconds.Should().Be(120);
        outcome.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public async Task EditAsync_same_content_returns_Success_without_discord_call()
    {
        var (svc, msgRepo, _, _, _, discord, audit, broadcaster, _, _) = Build();

        const string existingContent = "hello world";
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(content: existingContent));

        // Call with exactly the same content (trimmed).
        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, existingContent);

        result.Should().BeOfType<EditMessageOutcome.Success>();

        discord.Verify(d => d.EditWebhookMessageAsync(
            It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        audit.Verify(a => a.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);

        broadcaster.Verify(b => b.MessageUpdatedAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditAsync_happy_path_returns_Success_and_calls_discord_and_audit_and_broadcast()
    {
        var (svc, msgRepo, _, webhookRepo, _, discord, audit, broadcaster, _, _) = Build();

        const string oldContent = "old content";
        const string newContent = "new content";
        var fixedNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        svc._now = () => fixedNow;

        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(content: oldContent));

        webhookRepo.Setup(r => r.GetByChannelAsync(TenantId, ChannelId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeWebhook());

        var discordInfo = new DiscordMessageInfo(
            Id: (ulong)DiscordMessageId,
            AuthorId: 99UL,
            AuthorName: "alice",
            AuthorIsWebhook: true,
            Content: newContent,
            SentAt: DateTimeOffset.UtcNow,
            EditedAt: fixedNow);

        discord.Setup(d => d.EditWebhookMessageAsync(
                    WebhookId, "fake-webhook-token", (ulong)DiscordMessageId, newContent,
                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(discordInfo);

        msgRepo.Setup(r => r.UpdateContentAsync(TenantId, MessageId, newContent, fixedNow,
                    It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        var result = await svc.EditAsync(TenantId, ActorUserId, MessageId, newContent);

        result.Should().BeOfType<EditMessageOutcome.Success>();

        discord.Verify(d => d.EditWebhookMessageAsync(
            WebhookId, "fake-webhook-token", (ulong)DiscordMessageId, newContent,
            It.IsAny<CancellationToken>()), Times.Once);

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e =>
                e.EventType == AuditEventTypes.MessageEditSelf &&
                e.Payload != null &&
                (string)e.Payload["previousContent"]! == oldContent &&
                (string)e.Payload["newContent"]! == newContent),
            It.IsAny<CancellationToken>()), Times.Once);

        broadcaster.Verify(b => b.MessageUpdatedAsync(
            TenantId, DiscordMessageId, newContent, fixedNow,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
