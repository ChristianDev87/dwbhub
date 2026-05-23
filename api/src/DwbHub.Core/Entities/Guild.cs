namespace DwbHub.Core.Entities;

/// <summary>
/// Guild — a single Discord-server registration owned by a tenant.
/// `Id` is the internal BIGINT used for FK joins; never expose in API responses.
/// `PublicId` is the UUID that appears in URLs and audit payloads.
/// Foundation-Spec §0 enforces 1 tenant : N guilds; cross-tenant access is blocked
/// at the middleware layer (Plan 0.6).
/// </summary>
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
