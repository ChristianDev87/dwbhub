using DwbHub.Core.Entities;

namespace DwbHub.Application.Tenancy;

/// <summary>
/// Scoped resolver populated by TenantResolverMiddleware when a request URL
/// matches `/api/t/{slug}/g/{publicId}/...`. For tenant-only routes (no `/g/`
/// segment) or non-tenant routes, Current stays null and IsResolved is false.
/// Pattern mirrors <see cref="ITenantContext"/>: populated by TenantResolverMiddleware for guild-scoped routes only.
/// </summary>
public interface IGuildContext
{
    Guild? Current { get; }
    bool IsResolved => Current is not null;
}
