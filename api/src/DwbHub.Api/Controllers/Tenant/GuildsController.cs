using System.Security.Claims;
using DwbHub.Application.Audit;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

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
    IAuditWriter auditWriter) : ControllerBase
{
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
                RegisteredAt: DateTimeOffset.UtcNow));
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            return Conflict(new { error = "guild_already_registered" });
        }
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List(string slug, CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");

        var rows = await guilds.ListByTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        var responses = rows.Select(g => new GuildResponse(
            PublicId: g.PublicId,
            DiscordGuildId: g.DiscordGuildId,
            DisplayName: g.DisplayName,
            IsActive: g.IsActive,
            RegisteredAt: g.RegisteredAt)).ToList();

        return Ok(new GuildListResponse(responses));
    }

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

    private long? ExtractUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out var v) ? v : null;
    }
}
