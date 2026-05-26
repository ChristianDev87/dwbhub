namespace DwbHub.Core.Entities;

/// <summary>
/// A user within a tenant. The (TenantId, Email) pair is unique;
/// exactly one Owner per tenant is enforced by a partial UNIQUE index.
/// Timestamps use DateTimeOffset to match the database's TIMESTAMPTZ columns.
/// </summary>
/// <param name="Id">Internal primary key. Never expose in API responses.</param>
/// <param name="TenantId">Owning tenant. All queries must be scoped to this value.</param>
/// <param name="Email">Login email. Unique per tenant (CITEXT).</param>
/// <param name="EmailVerifiedAt">Timestamp when the user confirmed their email, or null if unverified.</param>
/// <param name="PasswordHash">BCrypt hash of the user's current password.</param>
/// <param name="DisplayName">Human-readable name shown in the UI.</param>
/// <param name="Role">Permission level within the tenant.</param>
/// <param name="IsActive">False when the account has been suspended or deactivated.</param>
/// <param name="CreatedAt">Row creation timestamp (TIMESTAMPTZ).</param>
/// <param name="UpdatedAt">Last modification timestamp (TIMESTAMPTZ).</param>
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
