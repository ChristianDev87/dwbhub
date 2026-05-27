using System.Security.Claims;
using DwbHub.Application.Messaging;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Tenant-scoped message endpoints. The TenantResolverMiddleware has already
/// validated tenant slug + JWT.tid before this controller runs.
/// Both endpoints require an authenticated tenant user.
///
/// Channel resolution is always scoped by tenant_id; a channel belonging to
/// another tenant returns 404 (not 403) to prevent cross-tenant information
/// disclosure.
/// </summary>
[Authorize]
[ApiController]
[Route("/api/t/{slug}/channels/{channelPublicId:guid}/messages")]
public sealed class MessagesController(
    IMessageService messageService,
    IGuildChannelRepository channels,
    ITenantContext tenantContext,
    IUserRepository users,
    ILogger<MessagesController> logger) : ControllerBase
{
    /// <summary>
    /// Send a message via the channel's registered Discord webhook.
    /// Persists with via_dwbhub=true and dwbhub_user_id=userId.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<SendMessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Send(
        string slug,
        Guid channelPublicId,
        [FromBody] SendMessageRequest req,
        CancellationToken ct)
    {
        _ = slug;

        if (!ModelState.IsValid)
            return BadRequest(new { error = "invalid_request" });

        // Reject whitespace-only content even though [Required] passes it.
        if (string.IsNullOrWhiteSpace(req.Content))
            return BadRequest(new { error = "content_required" });

        var userId = ExtractUserId();
        if (userId is null)
            return Unauthorized(new { error = "missing_sub_claim" });

        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");

        // Resolve the user to get DisplayName (NOT from JWT — JWT does not carry DisplayName).
        var user = await users.GetByIdAsync(tenant.Id, userId.Value, ct).ConfigureAwait(false);
        if (user is null)
            return Unauthorized(new { error = "user_not_found" });

        var channel = await channels.GetByPublicIdAsync(tenant.Id, channelPublicId, ct).ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        if (!channel.IsBridged)
            return Conflict(new { error = "channel_not_bridged" });

        try
        {
            var result = await messageService.SendOutboundAsync(
                tenant.Id,
                channel.Id,
                userId.Value,
                user.DisplayName,
                req.Content.Trim(),
                ct).ConfigureAwait(false);

            return StatusCode(StatusCodes.Status201Created,
                new SendMessageResponse(result.Id, result.PublicId, result.DiscordMessageId, result.SentAt));
        }
        catch (DiscordRateLimitException ex)
        {
            logger.LogWarning(
                "Discord rate limit hit for channel {ChannelId} tenant {TenantId}: retry after {Seconds}s",
                channel.Id, tenant.Id, ex.RetryAfterSeconds);
            Response.Headers["Retry-After"] = ex.RetryAfterSeconds.ToString();
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "discord_rate_limited", detail = "Discord webhook rate limit hit, retry shortly." });
        }
        catch (DiscordPermissionException ex)
        {
            logger.LogWarning(
                "Discord permission denied for channel {ChannelId} tenant {TenantId}: {Message}",
                channel.Id, tenant.Id, ex.Message);
            return Conflict(
                new
                {
                    error = "bot_missing_permission",
                    detail = "Bot lacks MANAGE_WEBHOOKS or SEND_MESSAGES on this channel.",
                });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No webhook"))
        {
            logger.LogWarning(
                "No webhook configured for channel {ChannelId} tenant {TenantId}",
                channel.Id, tenant.Id);
            return Conflict(
                new
                {
                    error = "channel_no_webhook",
                    detail = "No webhook is configured for this channel. Enable the bridge first.",
                });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            logger.LogWarning(
                "Webhook gone for channel {ChannelId} tenant {TenantId}: {Message}",
                channel.Id, tenant.Id, ex.Message);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "webhook_lost",
                    detail = "The Discord webhook was lost. Retry after recreating the bridge.",
                });
        }
    }

    /// <summary>
    /// Paginated message history for a channel. Returns rows newest-first.
    /// Use the returned NextBefore snowflake as the ?before= cursor for the next page.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<MessageHistoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory(
        string slug,
        Guid channelPublicId,
        [FromQuery] int limit = 50,
        [FromQuery] long? before = null,
        CancellationToken ct = default)
    {
        _ = slug;

        if (limit < 1 || limit > 100) limit = 50;

        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");

        var channel = await channels.GetByPublicIdAsync(tenant.Id, channelPublicId, ct).ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        var rows = await messageService.ListHistoryAsync(tenant.Id, channel.Id, before, limit, ct)
            .ConfigureAwait(false);

        var items = rows.Select(m => new MessageHistoryItem(
            Id: m.Id,
            PublicId: m.PublicId,
            AuthorName: m.DiscordAuthorName,
            Content: m.Content,
            SentAt: m.SentAt,
            EditedAt: m.EditedAt,
            ViaDwbhub: m.ViaDwbhub,
            DiscordMessageId: m.DiscordMessageId))
            .ToList();

        // NextBefore = oldest snowflake in this page; null when end of history reached.
        long? nextBefore = rows.Count == limit ? rows[^1].DiscordMessageId : null;

        return Ok(new MessageHistoryResponse(items, nextBefore));
    }

    /// <summary>
    /// Edit a message previously sent via DwbHub. Only the original author may edit.
    /// The edit window is 10 minutes from the original send time; beyond that a 422 is returned.
    /// </summary>
    [HttpPatch("{messagePublicId:guid}")]
    [ProducesResponseType<EditMessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<EditWindowExpiredResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Edit(
        string slug,
        Guid channelPublicId,
        Guid messagePublicId,
        [FromBody] EditMessageRequest req,
        CancellationToken ct)
    {
        _ = slug;

        if (!ModelState.IsValid)
            return BadRequest(new { error = "invalid_request" });

        if (string.IsNullOrWhiteSpace(req.Content))
            return BadRequest(new { error = "content_required" });

        var userId = ExtractUserId();
        if (userId is null)
            return Unauthorized(new { error = "missing_sub_claim" });

        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");

        var channel = await channels.GetByPublicIdAsync(tenant.Id, channelPublicId, ct).ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        var result = await messageService.EditOutboundAsync(
            tenant.Id, channel.Id, messagePublicId, userId.Value, req.Content.Trim(), ct)
            .ConfigureAwait(false);

        return result switch
        {
            EditMessageResult.Success s => Ok(new EditMessageResponse(
                Id: s.UpdatedMessage.Id,
                PublicId: s.UpdatedMessage.PublicId,
                AuthorName: s.UpdatedMessage.DiscordAuthorName,
                Content: s.UpdatedMessage.Content,
                SentAt: s.UpdatedMessage.SentAt,
                EditedAt: s.UpdatedMessage.EditedAt,
                ViaDwbhub: s.UpdatedMessage.ViaDwbhub,
                DiscordMessageId: s.UpdatedMessage.DiscordMessageId)),
            EditMessageResult.NotFound => NotFound(new { error = "message_not_found" }),
            EditMessageResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden,
                new { error = "forbidden" }),
            EditMessageResult.EditWindowExpired => UnprocessableEntity(
                new EditWindowExpiredResponse()),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// Delete a message previously sent via DwbHub.
    /// The original author OR a tenant Owner may delete.
    /// Hard-deletes from Discord (idempotent on 404) and soft-deletes in our DB.
    /// </summary>
    [HttpDelete("{messagePublicId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delete(
        string slug,
        Guid channelPublicId,
        Guid messagePublicId,
        CancellationToken ct)
    {
        _ = slug;

        var userId = ExtractUserId();
        if (userId is null)
            return Unauthorized(new { error = "missing_sub_claim" });

        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");

        // Resolve the user to get their role (Owner check for cross-author delete).
        var user = await users.GetByIdAsync(tenant.Id, userId.Value, ct).ConfigureAwait(false);
        if (user is null)
            return Unauthorized(new { error = "user_not_found" });

        var channel = await channels.GetByPublicIdAsync(tenant.Id, channelPublicId, ct).ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        var result = await messageService.DeleteOutboundAsync(
            tenant.Id, channel.Id, messagePublicId, userId.Value, user.Role, ct)
            .ConfigureAwait(false);

        return result switch
        {
            DeleteMessageResult.Success => NoContent(),
            DeleteMessageResult.NotFound => NotFound(new { error = "message_not_found" }),
            DeleteMessageResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden,
                new { error = "forbidden" }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private long? ExtractUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out var v) ? v : null;
    }
}
