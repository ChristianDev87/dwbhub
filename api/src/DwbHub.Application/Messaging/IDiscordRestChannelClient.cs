namespace DwbHub.Application.Messaging;

/// <summary>
/// Abstraction over the Discord REST API for channel and message operations.
/// All read methods accept a bot token so the caller controls which guild credentials to use.
/// Rate-limit errors surface as <see cref="DiscordRateLimitException"/>.
/// Permission errors surface as <see cref="DiscordPermissionException"/>.
/// </summary>
public interface IDiscordRestChannelClient
{
    /// <summary>List all channels visible to the bot in a guild.</summary>
    Task<IReadOnlyList<DiscordChannelInfo>> ListChannelsAsync(
        string botToken,
        ulong discordGuildId,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch up to <paramref name="limit"/> messages ordered newest-first.
    /// Pass <paramref name="beforeSnowflake"/> to paginate (Discord REST
    /// <c>before=&lt;id&gt;</c>); pass null to get the most-recent page.
    /// </summary>
    Task<IReadOnlyList<DiscordMessageInfo>> GetMessagesAsync(
        string botToken,
        ulong discordChannelId,
        ulong? beforeSnowflake,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Create a webhook in a channel. Returns the Discord webhook id and token.
    /// The caller is responsible for encrypting the token before storing it.
    /// </summary>
    Task<DiscordWebhookCreated> CreateWebhookAsync(
        string botToken,
        ulong discordChannelId,
        string name,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a webhook by its id and token pair. Does not require bot auth.
    /// Returns true iff the delete succeeded; false if the webhook was already
    /// gone (Discord 404 is treated as success for idempotent cleanup).
    /// </summary>
    Task<bool> DeleteWebhookAsync(
        ulong webhookId,
        string webhookToken,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a webhook to post a message. Uses <c>?wait=true</c> so Discord
    /// returns the created message snowflake. Throws
    /// <see cref="WebhookGoneException"/> on Discord 404 so the caller can
    /// recreate the webhook and retry.
    /// </summary>
    Task<DiscordMessageInfo> ExecuteWebhookAsync(
        ulong webhookId,
        string webhookToken,
        string username,
        string content,
        CancellationToken ct = default);

    /// <summary>
    /// Edit a message previously posted by this webhook. Discord supports
    /// only the content field for webhook-message edits (username, avatar,
    /// embeds, attachments are not editable post-hoc). Returns the updated
    /// message metadata. Throws <see cref="WebhookGoneException"/> when
    /// Discord returns 404 (either the webhook or the message was deleted).
    /// </summary>
    Task<DiscordMessageInfo> EditWebhookMessageAsync(
        ulong webhookId,
        string webhookToken,
        ulong messageId,
        string newContent,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a message previously posted by this webhook. Returns true iff
    /// the delete succeeded; false if the message was already gone (Discord
    /// 404 treated as idempotent success — same convention as
    /// <see cref="DeleteWebhookAsync"/>).
    /// </summary>
    Task<bool> DeleteWebhookMessageAsync(
        ulong webhookId,
        string webhookToken,
        ulong messageId,
        CancellationToken ct = default);
}

/// <summary>Discord channel metadata returned from the channel-list sync.</summary>
/// <param name="Id">Discord snowflake ID of the channel.</param>
/// <param name="Name">Display name of the channel.</param>
/// <param name="Type">Discord channel type integer (0 = text, 2 = voice, etc.).</param>
/// <param name="Position">Zero-based sort position within the guild channel list.</param>
public sealed record DiscordChannelInfo(ulong Id, string Name, short Type, int Position);

/// <summary>Discord message metadata for backfill and history queries.</summary>
/// <param name="Id">Discord snowflake ID of the message.</param>
/// <param name="AuthorId">Discord snowflake ID of the message author.</param>
/// <param name="AuthorName">Display name of the message author at send time.</param>
/// <param name="AuthorIsWebhook">True when the message was posted by a webhook rather than a real user.</param>
/// <param name="Content">Plain-text content of the message.</param>
/// <param name="SentAt">UTC timestamp when the message was originally sent.</param>
/// <param name="EditedAt">UTC timestamp of the most-recent edit, or <c>null</c> if never edited.</param>
public sealed record DiscordMessageInfo(
    ulong Id,
    ulong AuthorId,
    string AuthorName,
    bool AuthorIsWebhook,
    string Content,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt);

/// <summary>Webhook id and plaintext token returned by Discord on webhook creation.</summary>
/// <param name="WebhookId">Discord snowflake ID of the newly created webhook.</param>
/// <param name="WebhookToken">Plaintext webhook token; must be encrypted before storage.</param>
public sealed record DiscordWebhookCreated(ulong WebhookId, string WebhookToken);

/// <summary>
/// Thrown when Discord returns HTTP 429 (rate-limited). Callers should surface
/// the <see cref="RetryAfterSeconds"/> to the upstream client via Retry-After.
/// </summary>
public sealed class DiscordRateLimitException(int retryAfterSeconds, string message)
    : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Thrown when the bot lacks permissions to perform a Discord REST operation
/// (HTTP 403 or 401 from Discord's API).
/// </summary>
public sealed class DiscordPermissionException(string message) : Exception(message);

/// <summary>
/// Thrown when a Discord webhook returns 404 during execution.
/// MessageService catches this to trigger recreate-and-retry.
/// </summary>
public sealed class WebhookGoneException(ulong webhookId)
    : Exception($"Discord webhook {webhookId} not found (404).")
{ }
