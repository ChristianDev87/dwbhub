namespace DwbHub.Core.Entities;

/// <summary>
/// Tenant — the root of every multi-tenant query.
/// One DwbHub deployment hosts N tenants; each tenant has its own users,
/// guilds, audit log, etc. Slugs are case-insensitively unique (CITEXT).
/// `Locale` defaults to "de" and gates the default language used
/// when sending tenant-scoped emails where no explicit Accept-Language is
/// available. Timestamps are <see cref="DateTimeOffset"/> to match the
/// database's <c>TIMESTAMPTZ</c> columns and avoid timezone-ambiguity downstream.
/// </summary>
/// <param name="Id">Internal primary key. Never expose in API responses.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Slug">URL-safe identifier used in route prefixes (case-insensitive via CITEXT).</param>
/// <param name="Locale">BCP 47 language tag for default email locale (e.g. <c>"de"</c>, <c>"en"</c>).</param>
/// <param name="CreatedAt">Row creation timestamp (TIMESTAMPTZ).</param>
/// <param name="UpdatedAt">Last modification timestamp (TIMESTAMPTZ).</param>
/// <param name="MessageEditWindowSeconds">
/// Per-tenant override for the outbound-message edit window in seconds.
/// <c>null</c> means "use system default" (currently 10 minutes).
/// Valid overrides are in the range [60, 31536000].
/// </param>
public sealed record Tenant(
    long Id,
    string Name,
    string Slug,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? MessageEditWindowSeconds = null);
