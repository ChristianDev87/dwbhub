using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;

namespace DwbHub.Core.Repositories;

/// <summary>
/// Persistence for encrypted Discord bot credentials. Every method is
/// tenant-scoped (defense-in-depth alongside the UNIQUE(guild_id) constraint).
/// Never logs or returns the plaintext token — that is the encryptor's job.
/// </summary>
public interface IGuildBotCredentialRepository
{
    /// <summary>
    /// EXISTS check used by <c>BotCredentialsController</c> to distinguish the
    /// <c>bot_credentials.added</c> vs <c>bot_credentials.rotated</c> audit
    /// events. 1 round-trip.
    /// </summary>
    Task<bool> ExistsForGuildAsync(long guildId, long tenantId, CancellationToken ct = default);

    /// <summary>
    /// UPSERT (insert-or-replace) via <c>INSERT … ON CONFLICT (guild_id) DO UPDATE</c>.
    /// Single round-trip. The WHERE clause on the UPDATE guards against cross-tenant
    /// replacement even though guild_id UNIQUE handles the logical constraint.
    /// </summary>
    Task UpsertAsync(
        long guildId,
        long tenantId,
        CipherEnvelope envelope,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the encrypted credential for a guild. Returns null if no credential
    /// is configured. Used by Plan 0.8 BotConnectionManager. 1 round-trip.
    /// </summary>
    Task<GuildBotCredential?> GetByGuildIdAsync(
        long guildId, long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the credential for a guild. Returns true iff a row was deleted.
    /// </summary>
    Task<bool> DeleteAsync(long guildId, long tenantId, CancellationToken ct = default);
}
