namespace DwbHub.Core.Entities;

/// <summary>
/// A user within a tenant. The (TenantId, Email) pair is unique;
/// exactly one Owner per tenant is enforced by a partial UNIQUE index.
/// Timestamps use DateTimeOffset to match the database's TIMESTAMPTZ columns.
/// </summary>
public sealed record User(
    long Id,
    long TenantId,
    string Email,
    DateTimeOffset? EmailVerifiedAt,
    string PasswordHash,
    string DisplayName,
    UserRole Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
