using Dapper;
using DwbHub.Core.Encryption;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

public sealed class GuildBotCredentialRepository(IDbConnectionFactory connectionFactory)
    : IGuildBotCredentialRepository
{
    public async Task<bool> ExistsForGuildAsync(long guildId, long tenantId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT EXISTS(
                SELECT 1 FROM guild_bot_credentials
                WHERE guild_id = @GuildId AND tenant_id = @TenantId
            );
            """;
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql,
                new { GuildId = guildId, TenantId = tenantId },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task UpsertAsync(
        long guildId, long tenantId, CipherEnvelope envelope, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO guild_bot_credentials (guild_id, tenant_id, nonce, ciphertext, tag)
            VALUES (@GuildId, @TenantId, @Nonce, @Ciphertext, @Tag)
            ON CONFLICT (guild_id) DO UPDATE
               SET nonce = EXCLUDED.nonce,
                   ciphertext = EXCLUDED.ciphertext,
                   tag = EXCLUDED.tag,
                   updated_at = now()
            WHERE guild_bot_credentials.tenant_id = @TenantId;
            """;
        await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new
                {
                    GuildId = guildId,
                    TenantId = tenantId,
                    Nonce = envelope.Nonce,
                    Ciphertext = envelope.Ciphertext,
                    Tag = envelope.Tag,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<GuildBotCredential?> GetByGuildIdAsync(
        long guildId, long tenantId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, guild_id, tenant_id, nonce, ciphertext, tag, created_at, updated_at
            FROM guild_bot_credentials
            WHERE guild_id = @GuildId AND tenant_id = @TenantId;
            """;
        return await conn.QuerySingleOrDefaultAsync<GuildBotCredential>(
            new CommandDefinition(sql,
                new { GuildId = guildId, TenantId = tenantId },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(long guildId, long tenantId, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            DELETE FROM guild_bot_credentials
            WHERE guild_id = @GuildId AND tenant_id = @TenantId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { GuildId = guildId, TenantId = tenantId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }
}
