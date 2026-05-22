using System.Net;
using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Per-user refresh-token storage. All queries that resolve a token by hash also
/// JOIN through users to enforce (tenant_id, user_id) consistency at the database
/// level — Postgres rejects rows where the join fails, so cross-tenant hash
/// collisions (already astronomically unlikely with SHA-256) become unreachable.
/// </summary>
public interface IRefreshTokenRepository
{
    /// <summary>Insert a brand-new token issued at login. Returns the row id.</summary>
    Task<long> InsertAsync(
        long tenantId, long userId, byte[] tokenHash, DateTimeOffset expiresAt,
        UserRole issuedRole, bool issuedWasActive,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default);

    /// <summary>
    /// Single-CTE rotation: looks up the old token by hash, validates not revoked
    /// + not expired + snapshot matches current user state, inserts the new row
    /// (with the user's current state as snapshot), and revokes the old row pointing
    /// `replaced_by_token_id` to the new one. Returns diagnostic flags the caller
    /// uses to derive a `RefreshOutcome`.
    /// </summary>
    Task<RotationResult> RotateAsync(
        byte[] oldHash, byte[] newHash, DateTimeOffset newExpiresAt,
        IPAddress? ip, string? userAgent,
        CancellationToken ct = default);

    /// <summary>Mark the token with this hash as revoked (idempotent). Returns true if a row was updated.</summary>
    Task<bool> RevokeByHashAsync(byte[] tokenHash, CancellationToken ct = default);

    /// <summary>Revoke the whole forward chain starting from this token id (WITH RECURSIVE).</summary>
    Task RevokeChainAsync(long startTokenId, CancellationToken ct = default);
}
