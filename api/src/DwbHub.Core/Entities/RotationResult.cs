namespace DwbHub.Core.Entities;

/// <summary>
/// Output of `IRefreshTokenRepository.RotateAsync`. Returned by the single CTE
/// in `RefreshTokenRepository`. All diagnostic flags can be null when the input
/// token wasn't found at all.
/// </summary>
/// <param name="NewTokenId">ID of the freshly inserted row, or null if rotation didn't run.</param>
/// <param name="OldTokenId">ID of the row that was presented, or null if it didn't exist.</param>
/// <param name="UserId">User ID joined from the old token, or null if not found.</param>
/// <param name="TenantId">Tenant ID joined from the old token, or null if not found.</param>
/// <param name="UserRole">Current `users.role` value, or null if not found.</param>
/// <param name="TokenFound">True iff a row matched the input hash.</param>
/// <param name="WasRevoked">True iff the matched row had `revoked_at IS NOT NULL`.</param>
/// <param name="NotExpired">True iff `expires_at > now()`. Null if no row.</param>
/// <param name="RightsUnchanged">True iff snapshot still matches current user state. Null if no row.</param>
public sealed record RotationResult(
    long? NewTokenId,
    long? OldTokenId,
    long? UserId,
    long? TenantId,
    string? UserRole,
    bool TokenFound,
    bool WasRevoked,
    bool? NotExpired,
    bool? RightsUnchanged);
