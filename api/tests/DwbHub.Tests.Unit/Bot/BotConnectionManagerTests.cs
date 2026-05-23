using DwbHub.Application.Audit;
using DwbHub.Application.Bot;
using DwbHub.Application.Encryption;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using DwbHub.Infrastructure.Bot;
using DwbHub.Tests.Integration.Bot;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DwbHub.Tests.Unit.Bot;

public sealed class BotConnectionManagerTests
{
    private static CipherEnvelope FakeEnvelope() => new(
        Nonce: new byte[12],
        Ciphertext: Enumerable.Repeat((byte)0xAA, 80).ToArray(),
        Tag: new byte[16]);

    private static Guild FakeGuild(long id, long tenantId, bool isActive = true) =>
        new(Id: id, PublicId: Guid.NewGuid(), TenantId: tenantId,
            DiscordGuildId: "1234567890123456789", DisplayName: $"G{id}",
            IsActive: isActive, RegisteredByUserId: 1,
            RegisteredAt: DateTimeOffset.UtcNow, LastConnectedAt: null,
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    private static GuildBotCredential FakeCredential(long guildId, long tenantId) =>
        new(Id: guildId, GuildId: guildId, TenantId: tenantId,
            Nonce: new byte[12], Ciphertext: Enumerable.Repeat((byte)0xAA, 80).ToArray(),
            Tag: new byte[16],
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    private static (BotConnectionManager Mgr,
                    FakeBotConnectionFactory Factory,
                    Mock<IGuildRepository> Guilds,
                    Mock<IGuildBotCredentialRepository> Creds,
                    Mock<IBotTokenEncryptor> Enc,
                    Mock<IAuditWriter> Audit) Build()
    {
        var factory = new FakeBotConnectionFactory();
        var guilds = new Mock<IGuildRepository>();
        var creds = new Mock<IGuildBotCredentialRepository>();
        var enc = new Mock<IBotTokenEncryptor>();
        var audit = new Mock<IAuditWriter>();

        enc.Setup(e => e.Decrypt(It.IsAny<CipherEnvelope>())).Returns("decrypted-token");

        var sp = new ServiceCollection()
            .AddSingleton(guilds.Object)
            .AddSingleton(creds.Object)
            .AddSingleton(enc.Object)
            .AddSingleton(audit.Object)
            .BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var mgr = new BotConnectionManager(factory, scopeFactory, NullLogger<BotConnectionManager>.Instance);
        return (mgr, factory, guilds, creds, enc, audit);
    }

    [Fact]
    public async Task StartAsync_loads_active_guilds_with_credentials_and_connects_each()
    {
        var (mgr, factory, guilds, creds, _, _) = Build();
        guilds.Setup(r => r.ListActiveWithCredentialsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<GuildIdTenantPair> {
                  new(GuildId: 10, TenantId: 1), new(GuildId: 20, TenantId: 1) });
        guilds.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(10, 1));
        guilds.Setup(r => r.GetByIdAsync(20, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(20, 1));
        creds.Setup(r => r.GetByGuildIdAsync(10, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(FakeCredential(10, 1));
        creds.Setup(r => r.GetByGuildIdAsync(20, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(FakeCredential(20, 1));

        await mgr.StartAsync(CancellationToken.None);

        factory.Created.Should().HaveCount(2);
        factory.Created[10].State.Should().Be(BotConnectionState.Connected);
        factory.Created[20].State.Should().Be(BotConnectionState.Connected);
    }

    [Fact]
    public async Task StartAsync_skips_inactive_guilds_returned_by_repo()
    {
        var (mgr, factory, guilds, creds, _, _) = Build();
        guilds.Setup(r => r.ListActiveWithCredentialsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<GuildIdTenantPair> { new(30, 1) });
        guilds.Setup(r => r.GetByIdAsync(30, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(30, 1, isActive: false));   // race: deactivated between list and connect
        creds.Setup(r => r.GetByGuildIdAsync(30, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(FakeCredential(30, 1));

        await mgr.StartAsync(CancellationToken.None);

        factory.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_skips_guilds_without_credentials()
    {
        var (mgr, factory, guilds, creds, _, _) = Build();
        guilds.Setup(r => r.ListActiveWithCredentialsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<GuildIdTenantPair> { new(40, 1) });
        guilds.Setup(r => r.GetByIdAsync(40, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(40, 1));
        creds.Setup(r => r.GetByGuildIdAsync(40, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync((GuildBotCredential?)null);   // race: deleted

        await mgr.StartAsync(CancellationToken.None);

        factory.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task OnCredentialsChangedAsync_disconnects_existing_and_connects_new()
    {
        var (mgr, factory, guilds, creds, _, _) = Build();
        guilds.Setup(r => r.GetByIdAsync(50, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(50, 1));
        creds.Setup(r => r.GetByGuildIdAsync(50, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(FakeCredential(50, 1));

        // First connection
        await mgr.OnCredentialsChangedAsync(50, CancellationToken.None);
        var first = factory.Created[50];
        first.State.Should().Be(BotConnectionState.Connected);

        // Trigger rotate
        await mgr.OnCredentialsChangedAsync(50, CancellationToken.None);
        first.DisconnectCalls.Should().BeGreaterThan(0);
        factory.Created[50].Should().NotBeSameAs(first);   // factory creates a fresh instance
    }

    [Fact]
    public async Task OnCredentialsRemovedAsync_disconnects_without_reconnect()
    {
        var (mgr, factory, guilds, creds, _, _) = Build();
        guilds.Setup(r => r.GetByIdAsync(60, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(60, 1));
        creds.Setup(r => r.GetByGuildIdAsync(60, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(FakeCredential(60, 1));

        await mgr.OnCredentialsChangedAsync(60, CancellationToken.None);
        await mgr.OnCredentialsRemovedAsync(60, CancellationToken.None);

        mgr.GetState(60).Should().BeNull();  // removed from dict on credentials-removed
        factory.Created[60].DisconnectCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OnGuildDeactivatedAsync_disconnects()
    {
        var (mgr, factory, guilds, creds, _, _) = Build();
        guilds.Setup(r => r.GetByIdAsync(70, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(70, 1));
        creds.Setup(r => r.GetByGuildIdAsync(70, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(FakeCredential(70, 1));

        await mgr.OnCredentialsChangedAsync(70, CancellationToken.None);
        await mgr.OnGuildDeactivatedAsync(70, CancellationToken.None);

        mgr.GetState(70).Should().BeNull();  // removed from dict on deactivation
    }

    [Fact]
    public async Task OnGuildActivatedAsync_connects_if_credentials_present()
    {
        var (mgr, factory, guilds, creds, _, _) = Build();
        guilds.Setup(r => r.GetByIdAsync(80, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(80, 1));
        creds.Setup(r => r.GetByGuildIdAsync(80, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(FakeCredential(80, 1));

        await mgr.OnGuildActivatedAsync(80, CancellationToken.None);

        mgr.GetState(80).Should().Be(BotConnectionState.Connected);
    }

    [Fact]
    public async Task OnManualReconnectAsync_force_reconnect_cycle()
    {
        var (mgr, factory, guilds, creds, _, audit) = Build();
        guilds.Setup(r => r.GetByIdAsync(90, It.IsAny<CancellationToken>()))
              .ReturnsAsync(FakeGuild(90, 1));
        creds.Setup(r => r.GetByGuildIdAsync(90, 1, It.IsAny<CancellationToken>()))
             .ReturnsAsync(FakeCredential(90, 1));

        await mgr.OnCredentialsChangedAsync(90, CancellationToken.None);
        var first = factory.Created[90];

        await mgr.OnManualReconnectAsync(90, actorUserId: 42, CancellationToken.None);

        first.DisconnectCalls.Should().BeGreaterThan(0);
        factory.Created[90].Should().NotBeSameAs(first);
        factory.Created[90].State.Should().Be(BotConnectionState.Connected);
    }
}
