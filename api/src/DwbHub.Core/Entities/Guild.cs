namespace DwbHub.Core.Entities;

/// <summary>
/// Guild — a single Discord-server registration owned by a tenant.
/// `Id` is the internal BIGINT used for FK joins; never expose in API responses.
/// `PublicId` is the UUID that appears in URLs and audit payloads.
/// One tenant can have multiple guilds; cross-tenant access is blocked at the middleware layer.
/// </summary>
/// <param name="Id">Internal primary key. Used for FK joins only; never expose in API responses.</param>
/// <param name="PublicId">UUID exposed in URLs and audit payloads.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="DiscordGuildId">Discord snowflake ID stored as TEXT (unique per tenant).</param>
/// <param name="DisplayName">Human-readable name shown in the UI.</param>
/// <param name="IsActive">False when the guild has been deactivated and its bot connection should be stopped.</param>
/// <param name="RegisteredByUserId">ID of the tenant user who registered this guild.</param>
/// <param name="RegisteredAt">Timestamp when the guild was first registered (TIMESTAMPTZ).</param>
/// <param name="LastConnectedAt">Timestamp of the most recent successful bot gateway connection, or null.</param>
/// <param name="CreatedAt">Row creation timestamp (TIMESTAMPTZ).</param>
/// <param name="UpdatedAt">Last modification timestamp (TIMESTAMPTZ).</param>
public sealed record Guild(
    long Id,
    Guid PublicId,
    long TenantId,
    string DiscordGuildId,
    string DisplayName,
    bool IsActive,
    long RegisteredByUserId,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
