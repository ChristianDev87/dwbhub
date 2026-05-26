using System.Security.Claims;
using DwbHub.Application.Audit;
using DwbHub.Application.Bot;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Repositories;
using DwbHub.Infrastructure.Bot;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Tenant-scoped Guild CRUD endpoints. The TenantResolverMiddleware has
/// already validated tenant slug + JWT.tid before this controller runs.
/// Owners are required for POST/DELETE; any authenticated tenant user may GET.
/// </summary>
[ApiController]
[Route("/api/t/{slug}/guilds")]
public sealed class GuildsController(
    ITenantContext tenantContext,
    IGuildRepository guilds,
    IAuditWriter auditWriter,
    BotConnectionManager connectionManager) : ControllerBase
{
    /// <summary>
    /// Register a new Discord guild for the tenant. Requires the Owner role.
    /// Returns 201 Created with the new guild's public ID, or 409 Conflict if the
    /// Discord guild ID is already registered.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Owner")]
    public async Task<IActionResult> Add(string slug, [FromBody] AddGuildRequest body, CancellationToken ct)
    {
        _ = slug;
        if (!ModelState.IsValid)
        {
            return BadRequest(new { error = "invalid_discord_guild_id" });
        }

        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");
        var actorUserId = ExtractUserId();

        try
        {
            var (_, publicId) = await guilds.CreateAsync(
                tenantId: tenant.Id,
                discordGuildId: body.DiscordGuildId,
                displayName: body.DisplayName.Trim(),
                registeredByUserId: actorUserId ?? throw new InvalidOperationException("missing sub claim"),
                ct: ct).ConfigureAwait(false);

            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: tenant.Id,
                ActorUserId: actorUserId,
                EventType: "guild.added",
                Payload: new Dictionary<string, object?>
                {
                    ["tenantSlug"] = tenant.Slug,
                    ["guildPublicId"] = publicId.ToString("D"),
                    ["discordGuildId"] = body.DiscordGuildId,
                    ["displayName"] = body.DisplayName.Trim(),
                },
                IpAddress: HttpContext.Connection.RemoteIpAddress,
                UserAgent: HttpContext.Request.Headers.UserAgent.ToString()),
                ct).ConfigureAwait(false);

            return StatusCode(StatusCodes.Status201Created, new GuildResponse(
                PublicId: publicId,
                DiscordGuildId: body.DiscordGuildId,
                DisplayName: body.DisplayName.Trim(),
                IsActive: true,
                RegisteredAt: DateTimeOffset.UtcNow,
                BotCredentialsConfigured: false,
                BotConnectionState: null));
        }
        catch (GuildAlreadyExistsException)
        {
            // GuildRepository raises GuildAlreadyExistsException via ON CONFLICT DO NOTHING to avoid 23505 postgres log noise.
            return Conflict(new { error = "guild_already_registered" });
        }
    }

    /// <summary>
    /// List all guilds registered for the tenant, including bot credential and connection status.
    /// Accessible by any authenticated tenant user.
    /// </summary>
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List(string slug, CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");

        var items = await guilds.ListByTenantWithStatusAsync(tenant.Id, ct).ConfigureAwait(false);
        var responses = items.Select(i =>
        {
            string? botState = null;
            if (i.BotCredentialsConfigured)
            {
                botState = connectionManager.GetState(i.Guild.Id) switch
                {
                    BotConnectionState.Disconnected => "disconnected",
                    BotConnectionState.Connecting => "connecting",
                    BotConnectionState.Connected => "connected",
                    BotConnectionState.TokenInvalid => "token_invalid",
                    BotConnectionState.Failed => "failed",
                    // Exhaustive: add new cases here if BotConnectionState gains members.
                    // null is the "manager hasn't seen this guild yet" case — leave botState as null.
                    null => null,
                    _ => null,
                };
            }
            return new GuildResponse(
                PublicId: i.Guild.PublicId,
                DiscordGuildId: i.Guild.DiscordGuildId,
                DisplayName: i.Guild.DisplayName,
                IsActive: i.Guild.IsActive,
                RegisteredAt: i.Guild.RegisteredAt,
                BotCredentialsConfigured: i.BotCredentialsConfigured,
                BotConnectionState: botState);
        }).ToList();

        return Ok(new GuildListResponse(responses));
    }

    /// <summary>
    /// Remove a guild from the tenant. Requires the Owner role.
    /// Returns 204 No Content on success, 404 when the guild is not found.
    /// </summary>
    [HttpDelete("{publicId:guid}")]
    [Authorize(Roles = "Owner")]
    public async Task<IActionResult> Remove(string slug, Guid publicId, CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");
        var actorUserId = ExtractUserId();

        var existing = await guilds.GetByPublicIdAsync(publicId, tenant.Id, ct).ConfigureAwait(false);
        if (existing is null) return NotFound(new { error = "guild_not_found" });

        var deleted = await guilds.DeleteAsync(publicId, tenant.Id, ct).ConfigureAwait(false);
        if (!deleted) return NotFound(new { error = "guild_not_found" });

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: actorUserId,
            EventType: "guild.removed",
            Payload: new Dictionary<string, object?>
            {
                ["tenantSlug"] = tenant.Slug,
                ["guildPublicId"] = publicId.ToString("D"),
                ["discordGuildId"] = existing.DiscordGuildId,
            },
            IpAddress: HttpContext.Connection.RemoteIpAddress,
            UserAgent: HttpContext.Request.Headers.UserAgent.ToString()),
            ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Activate a guild so the bot may connect to its Discord gateway. Requires the Owner role.
    /// Idempotent — returns 204 whether the guild was previously inactive or already active.
    /// </summary>
    [HttpPost("{publicId:guid}/activate")]
    [Authorize(Roles = "Owner")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(string slug, Guid publicId, CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");
        var actorUserId = ExtractUserId();

        var guild = await guilds.GetByPublicIdAsync(publicId, tenant.Id, ct).ConfigureAwait(false);
        if (guild is null) return NotFound(new { error = "guild_not_found" });

        var changed = await guilds.SetActiveAsync(guild.Id, tenant.Id, isActive: true, ct).ConfigureAwait(false);
        if (!changed) return NoContent(); // idempotent no-op

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: actorUserId,
            EventType: AuditEventTypes.GuildActivated,
            Payload: new Dictionary<string, object?>
            {
                ["guildPublicId"] = guild.PublicId.ToString("D"),
                ["tenantSlug"] = tenant.Slug,
            },
            IpAddress: HttpContext.Connection.RemoteIpAddress,
            UserAgent: HttpContext.Request.Headers.UserAgent.ToString()),
            ct).ConfigureAwait(false);

        _ = connectionManager.OnGuildActivatedAsync(guild.Id, CancellationToken.None);
        return NoContent();
    }

    /// <summary>
    /// Deactivate a guild, disconnecting its bot from the Discord gateway. Requires the Owner role.
    /// Idempotent — returns 204 whether the guild was previously active or already inactive.
    /// </summary>
    [HttpPost("{publicId:guid}/deactivate")]
    [Authorize(Roles = "Owner")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(string slug, Guid publicId, CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");
        var actorUserId = ExtractUserId();

        var guild = await guilds.GetByPublicIdAsync(publicId, tenant.Id, ct).ConfigureAwait(false);
        if (guild is null) return NotFound(new { error = "guild_not_found" });

        var changed = await guilds.SetActiveAsync(guild.Id, tenant.Id, isActive: false, ct).ConfigureAwait(false);
        if (!changed) return NoContent(); // idempotent no-op

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: actorUserId,
            EventType: AuditEventTypes.GuildDeactivated,
            Payload: new Dictionary<string, object?>
            {
                ["guildPublicId"] = guild.PublicId.ToString("D"),
                ["tenantSlug"] = tenant.Slug,
            },
            IpAddress: HttpContext.Connection.RemoteIpAddress,
            UserAgent: HttpContext.Request.Headers.UserAgent.ToString()),
            ct).ConfigureAwait(false);

        _ = connectionManager.OnGuildDeactivatedAsync(guild.Id, CancellationToken.None);
        return NoContent();
    }

    /// <summary>
    /// Trigger a manual bot reconnect for a guild. Requires the Owner role.
    /// Returns 400 Bad Request when the guild is deactivated — activate it first.
    /// </summary>
    [HttpPost("{publicId:guid}/bot/reconnect")]
    [Authorize(Roles = "Owner")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reconnect(string slug, Guid publicId, CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");
        var actorUserId = ExtractUserId();

        var guild = await guilds.GetByPublicIdAsync(publicId, tenant.Id, ct).ConfigureAwait(false);
        if (guild is null) return NotFound(new { error = "guild_not_found" });
        if (!guild.IsActive) return BadRequest(new { error = "Guild is paused; activate first." });

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: actorUserId,
            EventType: AuditEventTypes.BotManualReconnect,
            Payload: new Dictionary<string, object?>
            {
                ["guildPublicId"] = guild.PublicId.ToString("D"),
                ["tenantSlug"] = tenant.Slug,
            },
            IpAddress: HttpContext.Connection.RemoteIpAddress,
            UserAgent: HttpContext.Request.Headers.UserAgent.ToString()),
            ct).ConfigureAwait(false);

        _ = connectionManager.OnManualReconnectAsync(guild.Id, actorUserId ?? 0, CancellationToken.None);
        return NoContent();
    }

    private long? ExtractUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out var v) ? v : null;
    }
}
