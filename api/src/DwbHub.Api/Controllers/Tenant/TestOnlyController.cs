using DwbHub.Application.Messaging;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Test-only endpoints used by Playwright e2e specs to inject Discord events
/// (MessageReceived) without needing a real Discord bot or gateway connection.
///
/// NOTE: The edit and delete test-only endpoints have been removed. Use the real
/// PATCH /messages/{id} and DELETE /messages/{id} endpoints instead — they are
/// fully exercisable in fake-rest mode via FakeDiscordRestChannelClient.
///
/// GATING: every action returns 404 unless DWBHUB_DISCORD_TEST_MODE=fake-rest.
/// Production guard: every action also throws if ASPNETCORE_ENVIRONMENT=Production.
///
/// These endpoints do NOT expose any data — they only trigger state transitions
/// that are already reachable via the normal Discord gateway flow.
/// </summary>
[Authorize]
[ApiController]
[Route("/api/t/{slug}/test-only")]
public sealed class TestOnlyController(
    IMessageService messageService,
    IGuildChannelRepository channelRepo,
    ITenantContext tenantContext,
    IHostEnvironment hostEnv,
    ILogger<TestOnlyController> logger) : ControllerBase
{
    // ── Guards ─────────────────────────────────────────────────────────────────

    private static readonly bool TestModeActive =
        Environment.GetEnvironmentVariable("DWBHUB_DISCORD_TEST_MODE") == "fake-rest";

    private IActionResult? CheckTestModeGuard()
    {
        if (hostEnv.IsProduction())
            throw new InvalidOperationException(
                "TestOnlyController endpoints are FORBIDDEN in Production. " +
                "DWBHUB_DISCORD_TEST_MODE must never be set in production stacks.");

        if (!TestModeActive)
            return NotFound(new { error = "test_mode_not_active" });

        return null;
    }

    // ── POST .../test-only/messages/inject-received ───────────────────────────

    /// <summary>
    /// Inject a fake inbound MessageReceived event into a bridged channel.
    /// Creates a new message row and broadcasts it to SignalR clients.
    /// Useful for testing scroll-position preservation and new-message badge.
    /// </summary>
    [HttpPost("messages/inject-received")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InjectReceived(
        string slug,
        [FromBody] TestInjectReceivedRequest req,
        CancellationToken ct)
    {
        _ = slug;
        var guard = CheckTestModeGuard();
        if (guard is not null) return guard;

        var tenant = ResolveTenant();

        // Resolve channel by public ID.
        var channel = await channelRepo.GetByPublicIdAsync(tenant.Id, req.ChannelPublicId, ct)
            .ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        if (!channel.IsBridged)
            return NotFound(new { error = "channel_not_bridged" });

        // Generate a unique fake snowflake for this injected message.
        var millis = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ulong fakeSnowflake = 700000000000000000UL + (millis % 99999999999UL);

        var evt = new MessageReceivedEvent
        {
            TenantId = tenant.Id,
            GuildId = channel.GuildId,
            DiscordChannelId = channel.DiscordChannelId,
            DiscordMessageId = (long)fakeSnowflake,
            DiscordAuthorId = 300000000000000999L,
            DiscordAuthorName = req.AuthorName ?? "test-injector",
            AuthorIsWebhook = false,
            WebhookSourceId = null,
            Content = req.Content ?? "injected test message",
            SentAt = DateTimeOffset.UtcNow,
        };

        logger.LogInformation(
            "[test-only] Injecting MessageReceived into channel {ChannelId} tenant {TenantId}",
            channel.Id, tenant.Id);

        var persisted = await messageService.PersistInboundAsync(evt, ct).ConfigureAwait(false);

        return StatusCode(StatusCodes.Status201Created,
            new { messageId = persisted?.Id, discordMessageId = (long)fakeSnowflake });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private DwbHub.Core.Entities.Tenant ResolveTenant()
        => tenantContext.Current
            ?? throw new InvalidOperationException(
                "TenantContext not populated despite /api/t/ route.");
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

/// <summary>Request body for the test-only inject-received endpoint.</summary>
public sealed record TestInjectReceivedRequest
{
    public Guid ChannelPublicId { get; init; }
    public string? Content { get; init; }
    public string? AuthorName { get; init; }
}
