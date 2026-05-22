namespace DwbHub.Core.Entities;

/// <summary>
/// Tenant — the root of every multi-tenant query.
/// One DwbHub deployment hosts N tenants; each tenant has its own users,
/// guilds, audit log, etc. Slugs are case-insensitively unique (CITEXT).
/// Timestamps are <see cref="DateTimeOffset"/> to match the database's
/// <c>TIMESTAMPTZ</c> columns and avoid timezone-ambiguity downstream.
/// </summary>
public sealed record Tenant(
    long Id,
    string Name,
    string Slug,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
