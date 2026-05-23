using System.Security.Claims;
using DwbHub.Application.Audit;
using DwbHub.Application.Encryption;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Tenant-scoped Discord bot-credential endpoints. The TenantResolverMiddleware
/// has already validated tenant slug + JWT.tid before this controller runs.
/// All mutations require the Owner role; the GET status is exposed via
/// the existing GuildsController.List endpoint (no separate GET here).
/// </summary>
[ApiController]
[Route("/api/t/{slug}/guilds/{guildPublicId:guid}/bot-credentials")]
public sealed class BotCredentialsController(
    ITenantContext tenantContext,
    IGuildRepository guilds,
    IGuildBotCredentialRepository credentials,
    IBotTokenEncryptor encryptor,
    IAuditWriter auditWriter) : ControllerBase
{
    [HttpPut]
    [Authorize(Roles = "Owner")]
    public async Task<IActionResult> Set(
        string slug,
        Guid guildPublicId,
        [FromBody] PutBotCredentialsRequest body,
        CancellationToken ct)
    {
        _ = slug;
        if (!ModelState.IsValid)
        {
            return BadRequest(new { error = "invalid_bot_token_format" });
        }

        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");
        var guild = await guilds.GetByPublicIdAsync(guildPublicId, tenant.Id, ct).ConfigureAwait(false);
        if (guild is null) return NotFound(new { error = "guild_not_found" });

        var wasExisting = await credentials.ExistsForGuildAsync(guild.Id, tenant.Id, ct).ConfigureAwait(false);
        var envelope = encryptor.Encrypt(body.Token);
        await credentials.UpsertAsync(guild.Id, tenant.Id, envelope, ct).ConfigureAwait(false);

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: ExtractUserId(),
            EventType: wasExisting ? "bot_credentials.rotated" : "bot_credentials.added",
            Payload: new Dictionary<string, object?>
            {
                ["tenantSlug"] = tenant.Slug,
                ["guildPublicId"] = guildPublicId.ToString("D"),
                // SECURITY: never include token plaintext, ciphertext, nonce, or tag here.
            },
            IpAddress: HttpContext.Connection.RemoteIpAddress,
            UserAgent: HttpContext.Request.Headers.UserAgent.ToString()), ct).ConfigureAwait(false);

        return NoContent();
    }

    [HttpDelete]
    [Authorize(Roles = "Owner")]
    public async Task<IActionResult> Remove(
        string slug,
        Guid guildPublicId,
        CancellationToken ct)
    {
        _ = slug;
        var tenant = tenantContext.Current
            ?? throw new InvalidOperationException("TenantContext not populated despite /api/t/ route.");
        var guild = await guilds.GetByPublicIdAsync(guildPublicId, tenant.Id, ct).ConfigureAwait(false);
        if (guild is null) return NotFound(new { error = "guild_not_found" });

        var deleted = await credentials.DeleteAsync(guild.Id, tenant.Id, ct).ConfigureAwait(false);
        if (!deleted) return NotFound(new { error = "bot_credentials_not_configured" });

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: ExtractUserId(),
            EventType: "bot_credentials.removed",
            Payload: new Dictionary<string, object?>
            {
                ["tenantSlug"] = tenant.Slug,
                ["guildPublicId"] = guildPublicId.ToString("D"),
            },
            IpAddress: HttpContext.Connection.RemoteIpAddress,
            UserAgent: HttpContext.Request.Headers.UserAgent.ToString()), ct).ConfigureAwait(false);

        return NoContent();
    }

    private long? ExtractUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return long.TryParse(raw, out var v) ? v : null;
    }
}
