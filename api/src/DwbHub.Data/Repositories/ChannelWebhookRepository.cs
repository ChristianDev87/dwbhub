using Dapper;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

/// <summary>
/// Dapper-backed implementation of <see cref="IChannelWebhookRepository"/>.
/// </summary>
public sealed class ChannelWebhookRepository(IDbConnectionFactory connectionFactory) : IChannelWebhookRepository
{
    /// <inheritdoc/>
    public async Task<ChannelWebhook?> GetByChannelAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, channel_id, discord_webhook_id,
                   ciphertext, nonce, auth_tag, key_version,
                   created_by_user_id, created_at
            FROM channel_webhooks
            WHERE tenant_id  = @TenantId
              AND channel_id = @ChannelId;
            """;
        return await conn.QuerySingleOrDefaultAsync<ChannelWebhook>(
            new CommandDefinition(sql,
                new { TenantId = tenantId, ChannelId = channelId },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ChannelWebhook> InsertAsync(
        ChannelWebhook webhook,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            WITH ins AS (
                INSERT INTO channel_webhooks (
                    tenant_id, channel_id, discord_webhook_id,
                    ciphertext, nonce, auth_tag, key_version,
                    created_by_user_id
                )
                VALUES (
                    @TenantId, @ChannelId, @DiscordWebhookId,
                    @Ciphertext, @Nonce, @AuthTag, @KeyVersion,
                    @CreatedByUserId
                )
                RETURNING *
            )
            SELECT * FROM ins;
            """;
        return await conn.QuerySingleAsync<ChannelWebhook>(
            new CommandDefinition(sql,
                new
                {
                    webhook.TenantId,
                    webhook.ChannelId,
                    webhook.DiscordWebhookId,
                    webhook.Ciphertext,
                    webhook.Nonce,
                    webhook.AuthTag,
                    webhook.KeyVersion,
                    webhook.CreatedByUserId,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteByChannelAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            DELETE FROM channel_webhooks
            WHERE tenant_id  = @TenantId
              AND channel_id = @ChannelId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, ChannelId = channelId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }
}
