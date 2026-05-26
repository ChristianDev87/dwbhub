using Dapper;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

/// <summary>
/// Dapper-backed implementation of <see cref="IMessageRepository"/>.
/// </summary>
public sealed class MessageRepository(IDbConnectionFactory connectionFactory) : IMessageRepository
{
    /// <inheritdoc/>
    public async Task<Message?> InsertAsync(Message msg, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            WITH ins AS (
                INSERT INTO messages (
                    tenant_id, channel_id, discord_message_id, discord_author_id,
                    discord_author_name, via_dwbhub, dwbhub_user_id, content,
                    sent_at, edited_at, deleted_at
                )
                VALUES (
                    @TenantId, @ChannelId, @DiscordMessageId, @DiscordAuthorId,
                    @DiscordAuthorName, @ViaDwbhub, @DwbhubUserId, @Content,
                    @SentAt, @EditedAt, @DeletedAt
                )
                ON CONFLICT (tenant_id, discord_message_id) DO NOTHING
                RETURNING *
            )
            SELECT * FROM ins;
            """;
        return await conn.QuerySingleOrDefaultAsync<Message>(
            new CommandDefinition(sql,
                new
                {
                    msg.TenantId,
                    msg.ChannelId,
                    msg.DiscordMessageId,
                    msg.DiscordAuthorId,
                    msg.DiscordAuthorName,
                    msg.ViaDwbhub,
                    msg.DwbhubUserId,
                    msg.Content,
                    msg.SentAt,
                    msg.EditedAt,
                    msg.DeletedAt,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> ApplyEditAsync(
        long tenantId,
        long discordMessageId,
        string content,
        DateTimeOffset editedAt,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE messages
               SET content = @Content,
                   edited_at = @EditedAt,
                   updated_at = now()
             WHERE tenant_id = @TenantId
               AND discord_message_id = @DiscordMessageId
               AND deleted_at IS NULL;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, DiscordMessageId = discordMessageId, Content = content, EditedAt = editedAt },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> MarkDeletedAsync(
        long tenantId,
        long discordMessageId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE messages
               SET deleted_at = now(),
                   updated_at = now()
             WHERE tenant_id = @TenantId
               AND discord_message_id = @DiscordMessageId
               AND deleted_at IS NULL;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, DiscordMessageId = discordMessageId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<Message?> GetByInternalIdAsync(long tenantId, long id, CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, channel_id, discord_message_id, discord_author_id,
                   discord_author_name, via_dwbhub, dwbhub_user_id, content,
                   sent_at, edited_at, deleted_at, created_at, updated_at
            FROM messages
            WHERE tenant_id = @TenantId
              AND id = @Id;
            """;
        return await conn.QuerySingleOrDefaultAsync<Message>(
            new CommandDefinition(sql,
                new { TenantId = tenantId, Id = id },
                cancellationToken: ct))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Message>> ListByChannelBeforeAsync(
        long tenantId,
        long channelId,
        long? beforeSnowflake,
        int limit,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT id, tenant_id, channel_id, discord_message_id, discord_author_id,
                   discord_author_name, via_dwbhub, dwbhub_user_id, content,
                   sent_at, edited_at, deleted_at, created_at, updated_at
            FROM messages
            WHERE tenant_id  = @TenantId
              AND channel_id = @ChannelId
              AND deleted_at IS NULL
              AND (@BeforeSnowflake::bigint IS NULL OR discord_message_id < @BeforeSnowflake)
            ORDER BY discord_message_id DESC
            LIMIT @Limit;
            """;
        var rows = await conn.QueryAsync<Message>(
            new CommandDefinition(sql,
                new
                {
                    TenantId = tenantId,
                    ChannelId = channelId,
                    BeforeSnowflake = beforeSnowflake,
                    Limit = limit,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
