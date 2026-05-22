using System.Collections.Immutable;
using System.Net;
using DwbHub.Application.Audit;
using FluentAssertions;
using Moq;
using Xunit;

namespace DwbHub.Tests.Unit.Audit;

public sealed class AuditEventTests
{
    [Fact]
    public void AuditEvent_constructor_accepts_minimum_args()
    {
        var evt = new AuditEvent(
            TenantId: 1, ActorUserId: 42, EventType: "auth.login.success",
            Payload: new Dictionary<string, object?> { ["tenantSlug"] = "acme" });
        evt.IpAddress.Should().BeNull();
        evt.UserAgent.Should().BeNull();
    }

    [Fact]
    public void AuditEvent_payload_is_read_only_dictionary()
    {
        var evt = new AuditEvent(
            TenantId: null, ActorUserId: null, EventType: "system.test",
            Payload: new Dictionary<string, object?> { ["k"] = "v" });
        evt.Payload.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>();
    }

    [Fact]
    public async Task AuditWriter_invokes_repo_with_canonical_payload_and_correct_hash()
    {
        var repo = new Mock<DwbHub.Core.Repositories.IAuditLogRepository>();
        repo.Setup(r => r.InsertAsync(
                It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<IPAddress?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((123L, new byte[32]));

        var sut = new AuditWriter(repo.Object);
        var evt = new AuditEvent(
            TenantId: 1, ActorUserId: 42, EventType: "auth.login.success",
            Payload: new Dictionary<string, object?> { ["tenantSlug"] = "acme" },
            IpAddress: IPAddress.Parse("10.0.0.1"), UserAgent: "test-ua");

        var id = await sut.RecordAsync(evt);
        id.Should().Be(123L);

        repo.Verify(r => r.InsertAsync(
            1L, 42L, "auth.login.success",
            @"{""tenantSlug"":""acme""}",                            // canonicalized
            It.Is<byte[]>(h => h.Length == 32),                       // SHA-256 32 bytes
            It.IsAny<DateTimeOffset>(),
            It.Is<IPAddress>(ip => ip.ToString() == "10.0.0.1"),
            "test-ua",
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
