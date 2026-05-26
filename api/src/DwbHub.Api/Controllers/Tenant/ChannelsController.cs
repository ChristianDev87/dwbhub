using System.Security.Claims;
using DwbHub.Application.Audit;
using DwbHub.Application.Messaging;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Tenant-scoped channel endpoints for the ChannelsPage UI.
/// The TenantResolverMiddleware has already validated tenant slug + JWT
/// before this controller runs.
///
/// All guild/channel lookups are scoped by tenant_id; cross-tenant
/// resource access returns 404 (not 403) to prevent information disclosure.
///
/// Webhook token lifecycle is handled by IChannelWebhookService — the token
/// never appears in any response body, DTO, or log line from this controller.
/// </summary>
[Authorize]
[ApiController]
[Route("api/t/{slug}")]
public sealed class ChannelsController(
    IGuildChannelRepository channelRepo,
    IGuildRepository guildRepo,
    IChannelSyncService syncService,
    IChannelWebhookService webhookService,
    IChannelBackfillJobRepository backfillJobRepo,
    IBackgroundJobClient jobClient,
    IMessagesBroadcaster broadcaster,
    IAuditWriter audit,
    ITenantContext tenantContext,
    ILogger<ChannelsController> logger) : ControllerBase
{
    // ── GET guilds/{guildPublicId}/channels ───────────────────────────────────

    /// <summary>
    /// Lists all channels known for the guild, with bridge state and optional
    /// backfill job summary per channel.
    /// </summary>
    [HttpGet("guilds/{guildPublicId:guid}/channels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelListResponse>> List(
        string slug,
        Guid guildPublicId,
        CancellationToken ct)
    {
        _ = slug;
        var tenant = ResolveTenant();

        // Resolve guild — scoped by tenant_id for info-leak prevention.
        var guild = await guildRepo.GetByPublicIdAsync(guildPublicId, tenant.Id, ct)
            .ConfigureAwait(false);
        if (guild is null)
            return NotFound(new { error = "guild_not_found" });

        var channels = await channelRepo.ListByGuildAsync(tenant.Id, guild.Id, ct)
            .ConfigureAwait(false);

        // N+1 backfill lookup: one extra call per bridged channel.
        // Acceptable: channels per guild are bounded and this is not a hot path.
        var items = new List<ChannelListItem>(channels.Count);
        foreach (var ch in channels)
        {
            BackfillStatusItem? backfill = null;
            if (ch.IsBridged)
            {
                var job = await backfillJobRepo.GetByChannelAsync(tenant.Id, ch.Id, ct)
                    .ConfigureAwait(false);
                if (job is not null)
                    backfill = ToBackfillStatusItem(job);
            }
            items.Add(new ChannelListItem(
                PublicId: ch.PublicId,
                DiscordChannelId: ch.DiscordChannelId,
                Name: ch.Name,
                ChannelType: ch.ChannelType,
                Position: ch.Position,
                IsBridged: ch.IsBridged,
                BridgedAt: ch.BridgedAt,
                LastSyncedAt: ch.LastSyncedAt,
                Backfill: backfill));
        }

        return Ok(new ChannelListResponse(items));
    }

    // ── POST guilds/{guildPublicId}/channels/sync ─────────────────────────────

    /// <summary>
    /// Re-fetches all channels from Discord and upserts them into the local DB.
    /// Returns 204 No Content on success.
    /// </summary>
    [HttpPost("guilds/{guildPublicId:guid}/channels/sync")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Sync(
        string slug,
        Guid guildPublicId,
        CancellationToken ct)
    {
        _ = slug;
        var tenant = ResolveTenant();
        var userId = ExtractUserId();

        // Validate guild belongs to this tenant before sync.
        var guild = await guildRepo.GetByPublicIdAsync(guildPublicId, tenant.Id, ct)
            .ConfigureAwait(false);
        if (guild is null)
            return NotFound(new { error = "guild_not_found" });

        try
        {
            await syncService.SyncFromDiscordAsync(tenant.Id, guildPublicId, ct)
                .ConfigureAwait(false);
        }
        catch (DiscordPermissionException ex)
        {
            logger.LogWarning(
                "Bot lacks permission to sync channels for guild {GuildPublicId} tenant {TenantId}: {Message}",
                guildPublicId, tenant.Id, ex.Message);
            return Conflict(new { error = "bot_missing_permission", detail = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("bot credentials"))
        {
            return Conflict(new { error = "bot_credentials_missing", detail = "Configure the bot token first." });
        }

        // Audit: sync requested — actual sync_completed audit is emitted by ChannelSyncService.
        await audit.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: userId,
            EventType: AuditEventTypes.ChannelSyncRequested,
            Payload: new Dictionary<string, object?>
            {
                ["guild_id"] = guild.Id,
                ["guild_public_id"] = guildPublicId.ToString("D"),
            }), ct).ConfigureAwait(false);

        return NoContent();
    }

    // ── POST channels/{channelPublicId}/bridge ────────────────────────────────

    /// <summary>
    /// Enables the bridge for a channel: creates a Discord webhook, inserts
    /// the encrypted credentials, creates a pending backfill job, and enqueues
    /// the Hangfire backfill runner. Returns 202 Accepted with the backfill job ID.
    ///
    /// The Hangfire enqueue happens AFTER all DB operations have succeeded to
    /// prevent orphan jobs.
    /// </summary>
    [HttpPost("channels/{channelPublicId:guid}/bridge")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Bridge(
        string slug,
        Guid channelPublicId,
        CancellationToken ct)
    {
        _ = slug;
        var tenant = ResolveTenant();
        var userId = ExtractUserId();
        if (userId is null)
            return Unauthorized(new { error = "missing_sub_claim" });

        // Resolve channel — scoped by tenant_id.
        var channel = await channelRepo.GetByPublicIdAsync(tenant.Id, channelPublicId, ct)
            .ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        // 409 if already bridged.
        if (channel.IsBridged)
            return Conflict(new { error = "channel_already_bridged" });

        // Create Discord webhook + store encrypted credentials.
        try
        {
            await webhookService.CreateForChannelAsync(tenant.Id, channel.Id, userId.Value, ct)
                .ConfigureAwait(false);
        }
        catch (DiscordPermissionException ex)
        {
            logger.LogWarning(
                "Bot lacks MANAGE_WEBHOOKS for channel {ChannelId} tenant {TenantId}: {Message}",
                channel.Id, tenant.Id, ex.Message);
            return Conflict(new
            {
                error = "bot_missing_permission",
                detail = "Bot lacks MANAGE_WEBHOOKS permission on this channel.",
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("bot credentials"))
        {
            return Conflict(new { error = "bot_credentials_missing", detail = "Configure the bot token first." });
        }

        // Mark channel as bridged.
        await channelRepo.SetBridgedAsync(tenant.Id, channelPublicId, isBridged: true, ct)
            .ConfigureAwait(false);

        // Insert pending backfill job.
        var backfillJob = await backfillJobRepo.InsertPendingAsync(tenant.Id, channel.Id, ct)
            .ConfigureAwait(false);

        // Enqueue Hangfire job AFTER DB operations — prevents orphan jobs on DB failure.
        var hangfireJobId = jobClient.Enqueue<IBackfillRunner>(
            r => r.RunAsync(tenant.Id, backfillJob.Id, CancellationToken.None));

        // Persist Hangfire job ID for later cancellation.
        await backfillJobRepo.SetHangfireJobIdAsync(tenant.Id, backfillJob.Id, hangfireJobId, ct)
            .ConfigureAwait(false);

        // Audit.
        await audit.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: userId,
            EventType: AuditEventTypes.ChannelBridged,
            Payload: new Dictionary<string, object?>
            {
                ["channel_id"] = channel.Id,
                ["channel_public_id"] = channelPublicId.ToString("D"),
                ["backfill_job_id"] = backfillJob.Id,
            }), ct).ConfigureAwait(false);

        // Broadcast bridge state change to connected clients.
        await broadcaster.ChannelBridgeChangedAsync(
            tenant.Id, channelPublicId, isBridged: true, ct).ConfigureAwait(false);

        return StatusCode(StatusCodes.Status202Accepted,
            new { backfillJobId = backfillJob.Id });
    }

    // ── DELETE channels/{channelPublicId}/bridge ──────────────────────────────

    /// <summary>
    /// Disables the bridge for a channel: cancels any active backfill job,
    /// deletes the Discord webhook, and marks the channel as unbridged.
    ///
    /// Historical messages are preserved (soft-delete semantics: we only
    /// remove the bridge mechanism, not the message rows).
    /// </summary>
    [HttpDelete("channels/{channelPublicId:guid}/bridge")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unbridge(
        string slug,
        Guid channelPublicId,
        CancellationToken ct)
    {
        _ = slug;
        var tenant = ResolveTenant();
        var userId = ExtractUserId();

        // Resolve channel — scoped by tenant_id.
        var channel = await channelRepo.GetByPublicIdAsync(tenant.Id, channelPublicId, ct)
            .ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        if (!channel.IsBridged)
            return NotFound(new { error = "channel_not_bridged" });

        // Cancel any active backfill job.
        var existingJob = await backfillJobRepo.GetByChannelAsync(tenant.Id, channel.Id, ct)
            .ConfigureAwait(false);
        if (existingJob is not null
            && existingJob.Status is Core.Messaging.BackfillStatus.Pending
                or Core.Messaging.BackfillStatus.Running)
        {
            if (existingJob.HangfireJobId is not null)
            {
                try
                {
                    jobClient.Delete(existingJob.HangfireJobId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not delete Hangfire job {HangfireJobId} for backfill {JobId} — " +
                        "marking cancelled in DB regardless.",
                        existingJob.HangfireJobId, existingJob.Id);
                }
            }
            await backfillJobRepo.MarkCancelledAsync(tenant.Id, existingJob.Id, ct)
                .ConfigureAwait(false);
        }

        // Delete Discord webhook + remove DB row (idempotent).
        await webhookService.DeleteForChannelAsync(tenant.Id, channel.Id, ct)
            .ConfigureAwait(false);

        // Mark channel as unbridged.
        // NOTE: messages rows are NOT deleted — history is preserved.
        await channelRepo.SetBridgedAsync(tenant.Id, channelPublicId, isBridged: false, ct)
            .ConfigureAwait(false);

        // Audit.
        await audit.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: userId,
            EventType: AuditEventTypes.ChannelUnbridged,
            Payload: new Dictionary<string, object?>
            {
                ["channel_id"] = channel.Id,
                ["channel_public_id"] = channelPublicId.ToString("D"),
            }), ct).ConfigureAwait(false);

        // Broadcast bridge state change.
        await broadcaster.ChannelBridgeChangedAsync(
            tenant.Id, channelPublicId, isBridged: false, ct).ConfigureAwait(false);

        return NoContent();
    }

    // ── GET channels/{channelPublicId}/backfill-status ────────────────────────

    /// <summary>
    /// Returns the current state of the most recent backfill job for the channel.
    /// Returns 404 when no job exists.
    /// </summary>
    [HttpGet("channels/{channelPublicId:guid}/backfill-status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BackfillStatusResponse>> BackfillStatus(
        string slug,
        Guid channelPublicId,
        CancellationToken ct)
    {
        _ = slug;
        var tenant = ResolveTenant();

        // Resolve channel — scoped by tenant_id.
        var channel = await channelRepo.GetByPublicIdAsync(tenant.Id, channelPublicId, ct)
            .ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        var job = await backfillJobRepo.GetByChannelAsync(tenant.Id, channel.Id, ct)
            .ConfigureAwait(false);
        if (job is null)
            return NotFound(new { error = "no_backfill_job" });

        return Ok(new BackfillStatusResponse(ToBackfillStatusItem(job)));
    }

    // ── DELETE channels/{channelPublicId}/backfill-jobs/{jobId} ──────────────

    /// <summary>
    /// Cancels a specific backfill job. Deletes from Hangfire and marks the DB
    /// row as Cancelled. Returns 204 No Content.
    /// </summary>
    [HttpDelete("channels/{channelPublicId:guid}/backfill-jobs/{jobId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelBackfill(
        string slug,
        Guid channelPublicId,
        long jobId,
        CancellationToken ct)
    {
        _ = slug;
        var tenant = ResolveTenant();
        var userId = ExtractUserId();

        // Resolve channel — scoped by tenant_id.
        var channel = await channelRepo.GetByPublicIdAsync(tenant.Id, channelPublicId, ct)
            .ConfigureAwait(false);
        if (channel is null)
            return NotFound(new { error = "channel_not_found" });

        // Load the job — verify it belongs to this tenant + channel.
        var job = await backfillJobRepo.GetByIdAsync(tenant.Id, jobId, ct)
            .ConfigureAwait(false);
        if (job is null || job.ChannelId != channel.Id)
            return NotFound(new { error = "backfill_job_not_found" });

        // Cancel in Hangfire (best-effort).
        if (job.HangfireJobId is not null)
        {
            try
            {
                jobClient.Delete(job.HangfireJobId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not delete Hangfire job {HangfireJobId} for backfill {JobId} — " +
                    "marking cancelled in DB regardless.",
                    job.HangfireJobId, job.Id);
            }
        }

        // Mark cancelled in DB.
        await backfillJobRepo.MarkCancelledAsync(tenant.Id, jobId, ct)
            .ConfigureAwait(false);

        // Audit.
        await audit.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: userId,
            EventType: AuditEventTypes.ChannelBackfillCancelled,
            Payload: new Dictionary<string, object?>
            {
                ["backfill_job_id"] = jobId,
                ["channel_id"] = channel.Id,
                ["channel_public_id"] = channelPublicId.ToString("D"),
            }), ct).ConfigureAwait(false);

        return NoContent();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private DwbHub.Core.Entities.Tenant ResolveTenant()
        => tenantContext.Current
            ?? throw new InvalidOperationException(
                "TenantContext not populated despite /api/t/ route.");

    private long? ExtractUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out var v) ? v : null;
    }

    private static BackfillStatusItem ToBackfillStatusItem(ChannelBackfillJob job)
        => new(
            JobId: job.Id,
            Status: job.Status.ToString().ToLowerInvariant(),
            FetchedCount: job.FetchedCount,
            StartedAt: job.StartedAt,
            CompletedAt: job.CompletedAt);
}
