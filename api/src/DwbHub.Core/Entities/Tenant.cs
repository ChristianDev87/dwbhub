namespace DwbHub.Core.Entities;

/// <summary>
/// Tenant — the root of every multi-tenant query.
/// One DwbHub deployment hosts N tenants; each tenant has its own users,
/// guilds, audit log, etc. Slugs are case-insensitively unique (CITEXT).
/// `Locale` (Plan 0.3d) defaults to "de" and gates the default language used
/// when sending tenant-scoped emails where no explicit Accept-Language is
/// available. Timestamps are <see cref="DateTimeOffset"/> to match the
/// database's <c>TIMESTAMPTZ</c> columns and avoid timezone-ambiguity downstream.
/// </summary>
public sealed record Tenant(
    long Id,
    string Name,
    string Slug,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
