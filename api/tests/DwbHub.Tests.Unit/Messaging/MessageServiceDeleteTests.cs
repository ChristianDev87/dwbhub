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
/// Unit tests for <see cref="MessageService.DeleteAsync"/>.
/// All dependencies are mocked — no DB, no Discord.
/// </summary>
public sealed class MessageServiceDeleteTests
{
    private const long TenantId = 1L;
    private const long ActorUserId = 42L;
    private const long OtherUserId = 99L;
    private const long MessageId = 1001L;
    private const long ChannelId = 99L;
    private const long GuildId = 10L;
    private const long DiscordMessageId = 12345L;
    private const long DiscordChannelId = 555L;
    private const long WebhookId = 777L;

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static Message MakeOutboundMessage(long authorUserId = ActorUserId, DateTimeOffset? deletedAt = null) => new()
    {
        Id = MessageId,
        TenantId = TenantId,
        ChannelId = ChannelId,
        DiscordMessageId = DiscordMessageId,
        DiscordAuthorId = 999L,
        DiscordAuthorName = "alice",
        ViaDwbhub = true,
        DwbhubUserId = authorUserId,
        Content = "hello",
        SentAt = DateTimeOffset.UtcNow,
        DeletedAt = deletedAt,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Message MakeInboundMessage(DateTimeOffset? deletedAt = null) => new()
    {
        Id = MessageId,
        TenantId = TenantId,
        ChannelId = ChannelId,
        DiscordMessageId = DiscordMessageId,
        DiscordAuthorId = 999L,
        DiscordAuthorName = "discord-user",
        ViaDwbhub = false,
        DwbhubUserId = null,
        Content = "inbound message",
        SentAt = DateTimeOffset.UtcNow,
        DeletedAt = deletedAt,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GuildChannel MakeChannel() => new()
    {
        Id = ChannelId,
        TenantId = TenantId,
        GuildId = GuildId,
        PublicId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DiscordChannelId = DiscordChannelId,
        Name = "general",
        ChannelType = 0,
        Position = 0,
        IsBridged = true,
        LastSyncedAt = DateTimeOffset.UtcNow,
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

    private static Guild MakeGuild(bool? botCanManageMessages = true) => new(
        Id: GuildId,
        PublicId: Guid.NewGuid(),
        TenantId: TenantId,
        DiscordGuildId: "1234567890",
        DisplayName: "Test Guild",
        IsActive: true,
        RegisteredByUserId: 1,
        RegisteredAt: DateTimeOffset.UtcNow,
        LastConnectedAt: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        BotCanManageMessages: botCanManageMessages);

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

        msgRepo.Setup(r => r.SoftDeleteByIdAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

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
    public async Task DeleteAsync_unknown_returns_NotFound()
    {
        var (svc, msgRepo, _, _, _, _, _, _, _, _) = Build();
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync((Message?)null);

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Member", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.NotFound>();
    }

    [Fact]
    public async Task DeleteAsync_already_deleted_returns_AlreadyDeleted()
    {
        var (svc, msgRepo, _, _, _, _, _, _, _, _) = Build();
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(deletedAt: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Member", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.AlreadyDeleted>();
    }

    [Fact]
    public async Task DeleteAsync_self_outbound_returns_Success_uses_webhook_DELETE()
    {
        var (svc, msgRepo, channelRepo, webhookRepo, _, discord, audit, _, _, _) = Build();

        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(authorUserId: ActorUserId));

        webhookRepo.Setup(r => r.GetByChannelAsync(TenantId, ChannelId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeWebhook());

        discord.Setup(d => d.DeleteWebhookMessageAsync(
                    WebhookId, "fake-webhook-token", (ulong)DiscordMessageId,
                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        channelRepo.Setup(r => r.ListBridgedAsync(TenantId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<GuildChannel> { MakeChannel() });

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Member", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.Success>();

        discord.Verify(d => d.DeleteWebhookMessageAsync(
            WebhookId, "fake-webhook-token", (ulong)DiscordMessageId,
            It.IsAny<CancellationToken>()), Times.Once);

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageDeleteSelf),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_other_user_outbound_non_owner_returns_Forbidden()
    {
        var (svc, msgRepo, _, _, _, _, _, _, _, _) = Build();

        // Message authored by someone else; actor is Member (not Owner)
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(authorUserId: OtherUserId));

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Member", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.Forbidden>();
    }

    [Fact]
    public async Task DeleteAsync_other_user_outbound_owner_returns_Success_uses_webhook_DELETE_audits_moderation_outbound()
    {
        var (svc, msgRepo, channelRepo, webhookRepo, _, discord, audit, _, _, _) = Build();

        // Message authored by OtherUserId; actor is Owner
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(authorUserId: OtherUserId));

        webhookRepo.Setup(r => r.GetByChannelAsync(TenantId, ChannelId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeWebhook());

        discord.Setup(d => d.DeleteWebhookMessageAsync(
                    WebhookId, "fake-webhook-token", (ulong)DiscordMessageId,
                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        channelRepo.Setup(r => r.ListBridgedAsync(TenantId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<GuildChannel> { MakeChannel() });

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Owner", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.Success>();

        discord.Verify(d => d.DeleteWebhookMessageAsync(
            WebhookId, "fake-webhook-token", (ulong)DiscordMessageId,
            It.IsAny<CancellationToken>()), Times.Once);

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageDeleteModerationOutbound),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_inbound_owner_with_permission_returns_Success_uses_bot_DELETE()
    {
        var (svc, msgRepo, channelRepo, _, _, discord, audit, _, _, guilds) = Build();

        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeInboundMessage());

        channelRepo.Setup(r => r.ListBridgedAsync(TenantId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<GuildChannel> { MakeChannel() });

        guilds.Setup(g => g.GetByIdAsync(GuildId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeGuild(botCanManageMessages: true));

        discord.Setup(d => d.DeleteChannelMessageAsync(
                    (ulong)DiscordChannelId, (ulong)DiscordMessageId, GuildId,
                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Owner", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.Success>();

        discord.Verify(d => d.DeleteChannelMessageAsync(
            (ulong)DiscordChannelId, (ulong)DiscordMessageId, GuildId,
            It.IsAny<CancellationToken>()), Times.Once);

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageDeleteModerationInbound),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_inbound_owner_without_permission_returns_BotMissingPermission()
    {
        var (svc, msgRepo, channelRepo, _, _, _, _, _, _, guilds) = Build();

        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeInboundMessage());

        channelRepo.Setup(r => r.ListBridgedAsync(TenantId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<GuildChannel> { MakeChannel() });

        // Permission is false (cached as not having MANAGE_MESSAGES)
        guilds.Setup(g => g.GetByIdAsync(GuildId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeGuild(botCanManageMessages: false));

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Owner", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.BotMissingPermission>();
    }

    [Fact]
    public async Task DeleteAsync_inbound_owner_discord_returns_403_marks_permission_false_and_returns_BotMissingPermission()
    {
        var (svc, msgRepo, channelRepo, _, _, discord, _, _, _, guilds) = Build();

        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeInboundMessage());

        channelRepo.Setup(r => r.ListBridgedAsync(TenantId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<GuildChannel> { MakeChannel() });

        // Guild has cached permission = true, but Discord returns false (403 simulation)
        guilds.Setup(g => g.GetByIdAsync(GuildId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeGuild(botCanManageMessages: true));

        discord.Setup(d => d.DeleteChannelMessageAsync(
                    (ulong)DiscordChannelId, (ulong)DiscordMessageId, GuildId,
                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(false); // Discord returned 403

        guilds.Setup(g => g.UpdateBotPermissionsAsync(GuildId, TenantId, false, It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Owner", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.BotMissingPermission>();

        guilds.Verify(g => g.UpdateBotPermissionsAsync(
            GuildId, TenantId, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_inbound_non_owner_returns_Forbidden()
    {
        var (svc, msgRepo, _, _, _, _, _, _, _, _) = Build();

        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeInboundMessage());

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Member", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.Forbidden>();
    }

    [Fact]
    public async Task DeleteAsync_owner_deletes_own_outbound_takes_self_path_not_moderation_path()
    {
        // Actor is Owner AND author of the outbound message.
        // The self-delete path (isAuthor check) takes priority over mod-outbound.
        var (svc, msgRepo, channelRepo, webhookRepo, _, discord, audit, _, _, _) = Build();

        // Message authored by ActorUserId; actor role is Owner
        msgRepo.Setup(r => r.GetByInternalIdAsync(TenantId, MessageId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(MakeOutboundMessage(authorUserId: ActorUserId));

        webhookRepo.Setup(r => r.GetByChannelAsync(TenantId, ChannelId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(MakeWebhook());

        discord.Setup(d => d.DeleteWebhookMessageAsync(
                    WebhookId, "fake-webhook-token", (ulong)DiscordMessageId,
                    It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        channelRepo.Setup(r => r.ListBridgedAsync(TenantId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<GuildChannel> { MakeChannel() });

        var result = await svc.DeleteAsync(TenantId, ActorUserId, "Owner", MessageId);

        result.Should().BeOfType<DeleteMessageOutcome.Success>();

        // Must be MessageDeleteSelf, NOT MessageDeleteModerationOutbound
        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageDeleteSelf),
            It.IsAny<CancellationToken>()), Times.Once);

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == AuditEventTypes.MessageDeleteModerationOutbound),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
