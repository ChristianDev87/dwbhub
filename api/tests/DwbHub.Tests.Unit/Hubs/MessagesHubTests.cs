using System.Reflection;
using System.Security.Claims;
using DwbHub.Api.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DwbHub.Tests.Unit.Hubs;

/// <summary>
/// Unit tests for MessagesHub.
///
/// Security focus:
///   1. On connect, the connection is added to the correct tenant group derived
///      from the JWT "tid" claim — NOT from any client-supplied value.
///   2. When the tenant claim is missing or invalid, the connection is aborted
///      immediately and NO group add is attempted.
///   3. The hub declares zero client-callable methods so a future contributor
///      cannot accidentally introduce an endpoint that bypasses tenant scoping.
/// </summary>
public sealed class MessagesHubTests
{
    // ── helper: build a ClaimsPrincipal with a "tid" claim ─────────────────

    private static ClaimsPrincipal MakePrincipal(string? tidValue)
    {
        var claims = new List<Claim>();
        if (tidValue is not null)
            claims.Add(new Claim("tid", tidValue));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    // ── helper: wire a MessagesHub with controllable Context / Groups ───────

    private static (MessagesHub hub, Mock<IGroupManager> groups, Mock<HubCallerContext> context)
        BuildHub(ClaimsPrincipal principal, string connectionId = "conn-test-1")
    {
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.User).Returns(principal);

        var groups = new Mock<IGroupManager>();
        groups
            .Setup(g => g.AddToGroupAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = new MessagesHub(NullLogger<MessagesHub>.Instance);
        hub.Context = context.Object;
        hub.Groups = groups.Object;

        return (hub, groups, context);
    }

    // ── Test 1: connect with valid tenant claim → group join ────────────────

    [Fact]
    public async Task OnConnectedAsync_WithValidTenantClaim_AddsToTenantGroup()
    {
        var principal = MakePrincipal("42");
        var (hub, groups, context) = BuildHub(principal);

        await hub.OnConnectedAsync();

        groups.Verify(
            g => g.AddToGroupAsync("conn-test-1", "tenant:42", It.IsAny<CancellationToken>()),
            Times.Once,
            "connection must be added to the group derived from the JWT tid claim");

        // Context.Abort must NOT have been called
        context.Verify(c => c.Abort(), Times.Never,
            "a valid tenant claim must not abort the connection");
    }

    // ── Test 2: connect with missing/zero tenant claim → abort ──────────────

    [Theory]
    [InlineData(null)]          // tid claim absent
    [InlineData("")]            // empty string
    [InlineData("0")]           // zero — invalid (tenant IDs are > 0)
    [InlineData("-1")]          // negative — invalid
    [InlineData("not-a-number")]// non-numeric
    public async Task OnConnectedAsync_WithInvalidTenantClaim_AbortsConnection(string? tidValue)
    {
        var principal = MakePrincipal(tidValue);
        var (hub, groups, context) = BuildHub(principal);

        await hub.OnConnectedAsync();

        context.Verify(c => c.Abort(), Times.Once,
            "an unresolved or invalid tenant claim must abort the SignalR connection");

        groups.Verify(
            g => g.AddToGroupAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "no group join must occur when the tenant is unresolved");
    }

    // ── Test 3: no client-callable methods declared ──────────────────────────

    [Fact]
    public void Hub_HasNoClientCallableMethods()
    {
        // A client-callable method is any public instance method declared on
        // the subclass (DeclaredOnly) that is NOT one of the two framework
        // lifecycle overrides. The invariant: the hub is push-only.
        var methods = typeof(MessagesHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m =>
                m.Name is not nameof(MessagesHub.OnConnectedAsync) and
                          not nameof(MessagesHub.OnDisconnectedAsync))
            .ToArray();

        methods.Should().BeEmpty(
            "MessagesHub is push-only — any client-callable method would bypass " +
            "tenant scoping and is a security vulnerability");
    }
}
