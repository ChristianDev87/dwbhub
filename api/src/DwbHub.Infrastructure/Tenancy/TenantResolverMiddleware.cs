using System.Security.Claims;
using System.Text.RegularExpressions;
using DwbHub.Application.Audit;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using Microsoft.AspNetCore.Http;

namespace DwbHub.Infrastructure.Tenancy;

/// <summary>
/// Resolves the tenant — and optionally the guild — for incoming
/// /api/t/{slug}/... and /api/t/{slug}/g/{publicId}/... requests.
///
/// Match priority:
///   1. TenantGuildRegex  → combined single-CTE resolution via IGuildRepository.
///   2. TenantOnlyRegex   → tenant-only resolution via ITenantRepository.
///   3. otherwise         → no-op pass-through.
///
/// Failure modes (spec §4.4):
///   - Slug missing → 404 + auth.unknown_tenant_access.
///   - JWT.tid mismatch → 403 + auth.cross_tenant_access_blocked.
///   - Guild UUID not found within tenant → 404 + guild.unknown_access
///     (info-leak: same response shape as unknown tenant).
/// </summary>
public sealed class TenantResolverMiddleware(RequestDelegate next)
{
    private static readonly Regex TenantGuildRegex = new(
        @"^/api/t/([a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?)/g/" +
        @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})" +
        @"(?:/|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TenantOnlyRegex = new(
        @"^/api/t/([a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?)(?:/|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task InvokeAsync(
        HttpContext ctx,
        ITenantContext tenantCtx,
        IGuildContext guildCtx,
        ITenantRepository tenants,
        IGuildRepository guilds,
        IAuditWriter auditWriter)
    {
        var guildMatch = TenantGuildRegex.Match(ctx.Request.Path);
        if (guildMatch.Success)
        {
            var slug = guildMatch.Groups[1].Value.ToLowerInvariant();
            var publicId = Guid.Parse(guildMatch.Groups[2].Value);
            if (!await HandleGuildScopedAsync(ctx, tenantCtx, guildCtx, guilds, auditWriter, slug, publicId)
                    .ConfigureAwait(false))
            {
                return;
            }
            await next(ctx).ConfigureAwait(false);
            return;
        }

        var tenantMatch = TenantOnlyRegex.Match(ctx.Request.Path);
        if (tenantMatch.Success)
        {
            var slug = tenantMatch.Groups[1].Value.ToLowerInvariant();
            if (!await HandleTenantOnlyAsync(ctx, tenantCtx, tenants, auditWriter, slug)
                    .ConfigureAwait(false))
            {
                return;
            }
            await next(ctx).ConfigureAwait(false);
            return;
        }

        await next(ctx).ConfigureAwait(false);
    }

    private static async Task<bool> HandleTenantOnlyAsync(
        HttpContext ctx, ITenantContext tenantCtx, ITenantRepository tenants,
        IAuditWriter auditWriter, string slug)
    {
        var tenant = await tenants.GetBySlugAsync(slug, ctx.RequestAborted).ConfigureAwait(false);
        if (tenant is null)
        {
            await EmitUnknownTenantAuditAsync(ctx, auditWriter, slug).ConfigureAwait(false);
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "tenant_not_found" });
            return false;
        }
        ((TenantContext)tenantCtx).Current = tenant;
        return await ValidateJwtTidAsync(ctx, auditWriter, tenant, slug).ConfigureAwait(false);
    }

    private static async Task<bool> HandleGuildScopedAsync(
        HttpContext ctx, ITenantContext tenantCtx, IGuildContext guildCtx,
        IGuildRepository guilds, IAuditWriter auditWriter, string slug, Guid publicId)
    {
        var (tenant, guild) = await guilds.ResolveTenantAndGuildAsync(slug, publicId, ctx.RequestAborted)
            .ConfigureAwait(false);

        if (tenant is null)
        {
            await EmitUnknownTenantAuditAsync(ctx, auditWriter, slug).ConfigureAwait(false);
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "tenant_not_found" });
            return false;
        }

        ((TenantContext)tenantCtx).Current = tenant;

        if (!await ValidateJwtTidAsync(ctx, auditWriter, tenant, slug).ConfigureAwait(false))
        {
            return false;
        }

        if (guild is null)
        {
            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: tenant.Id,
                ActorUserId: ExtractActorUserId(ctx),
                EventType: "guild.unknown_access",
                Payload: new Dictionary<string, object?>
                {
                    ["tenantSlug"] = slug,
                    ["attemptedPublicId"] = publicId.ToString("D"),
                },
                IpAddress: ctx.Connection.RemoteIpAddress,
                UserAgent: ctx.Request.Headers.UserAgent.ToString()),
                ctx.RequestAborted).ConfigureAwait(false);

            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "guild_not_found" });
            return false;
        }

        ((GuildContext)guildCtx).Current = guild;
        return true;
    }

    private static async Task EmitUnknownTenantAuditAsync(
        HttpContext ctx, IAuditWriter auditWriter, string slug)
    {
        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: null,
            ActorUserId: null,
            EventType: "auth.unknown_tenant_access",
            Payload: new Dictionary<string, object?> { ["pathSlug"] = slug },
            IpAddress: ctx.Connection.RemoteIpAddress,
            UserAgent: ctx.Request.Headers.UserAgent.ToString()),
            ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<bool> ValidateJwtTidAsync(
        HttpContext ctx, IAuditWriter auditWriter, Tenant tenant, string slug)
    {
        var jwtTidStr = ctx.User.FindFirst("tid")?.Value;
        if (jwtTidStr is null) return true;
        if (!long.TryParse(jwtTidStr, out var jwtTid)) return true;
        if (jwtTid == tenant.Id) return true;

        await auditWriter.RecordAsync(new AuditEvent(
            TenantId: tenant.Id,
            ActorUserId: ExtractActorUserId(ctx),
            EventType: "auth.cross_tenant_access_blocked",
            Payload: new Dictionary<string, object?>
            {
                ["pathSlug"] = slug,
                ["pathTenantId"] = tenant.Id,
                ["jwtTenantId"] = jwtTid,
            },
            IpAddress: ctx.Connection.RemoteIpAddress,
            UserAgent: ctx.Request.Headers.UserAgent.ToString()),
            ctx.RequestAborted).ConfigureAwait(false);

        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "cross_tenant_access_denied" });
        return false;
    }

    private static long? ExtractActorUserId(HttpContext ctx)
    {
        var raw = ctx.User.FindFirst("sub")?.Value
                  ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(raw, out var v) ? v : null;
    }
}
