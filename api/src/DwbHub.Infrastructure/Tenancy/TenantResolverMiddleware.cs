using System.Security.Claims;
using System.Text.RegularExpressions;
using DwbHub.Application.Audit;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Repositories;
using Microsoft.AspNetCore.Http;

namespace DwbHub.Infrastructure.Tenancy;

/// <summary>
/// Resolves the tenant for incoming /api/t/{slug}/... requests:
///   1. Parses slug via path regex (same shape as SlugValidator).
///   2. Looks up tenant via ITenantRepository.GetBySlugAsync.
///   3. Stores in ITenantContext.Current for downstream services.
///   4. If JWT carries a "tid" claim that doesn't match: 403 + audit event.
///   5. If slug doesn't resolve to a tenant: 404 + audit event.
/// For paths NOT matching the prefix, the middleware is a no-op pass-through.
/// </summary>
public sealed class TenantResolverMiddleware(RequestDelegate next)
{
    private static readonly Regex PathRegex = new(
        @"^/api/t/([a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?)(?:/|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task InvokeAsync(
        HttpContext ctx,
        ITenantContext tenantCtx,
        ITenantRepository tenants,
        IAuditWriter auditWriter)
    {
        var match = PathRegex.Match(ctx.Request.Path);
        if (!match.Success)
        {
            await next(ctx).ConfigureAwait(false);
            return;
        }

        var slug = match.Groups[1].Value.ToLowerInvariant();
        var tenant = await tenants.GetBySlugAsync(slug, ctx.RequestAborted).ConfigureAwait(false);
        if (tenant is null)
        {
            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: null,
                ActorUserId: null,
                EventType: "auth.unknown_tenant_access",
                Payload: new Dictionary<string, object?> { ["pathSlug"] = slug },
                IpAddress: ctx.Connection.RemoteIpAddress,
                UserAgent: ctx.Request.Headers.UserAgent.ToString()),
                ctx.RequestAborted).ConfigureAwait(false);

            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "tenant_not_found" });
            return;
        }

        ((TenantContext)tenantCtx).Current = tenant;

        var jwtTidStr = ctx.User.FindFirst("tid")?.Value;
        if (jwtTidStr is not null
            && long.TryParse(jwtTidStr, out var jwtTid)
            && jwtTid != tenant.Id)
        {
            var actorUserIdStr = ctx.User.FindFirst("sub")?.Value
                                 ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            long? actorUserId = long.TryParse(actorUserIdStr, out var aid) ? aid : null;

            await auditWriter.RecordAsync(new AuditEvent(
                TenantId: tenant.Id,
                ActorUserId: actorUserId,
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
            return;
        }

        await next(ctx).ConfigureAwait(false);
    }
}
