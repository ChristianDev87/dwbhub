using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Discord;
using Discord.Rest;
using DwbHub.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Messaging;

/// <summary>
/// Discord REST client implementation using Discord.Net's <see cref="DiscordRestClient"/>
/// for authenticated read operations and a plain <see cref="HttpClient"/> for the
/// webhook-execute path (no bot auth required when posting with the webhook token).
///
/// Discord.Net 3.16 API notes:
///   - <c>DiscordRestClient.LoginAsync(TokenType.Bot, token)</c> must be called before use.
///   - <c>GetGuildChannelsAsync</c> returns <c>IReadOnlyCollection&lt;RestGuildChannel&gt;</c>.
///   - <c>GetChannelMessagesAsync</c> returns <c>IReadOnlyCollection&lt;IMessage&gt;</c>.
///   - <c>CreateWebhookAsync</c> is a method on <c>ITextChannel</c> — channel must be cast.
///   - Webhook execute / delete do NOT require bot auth; they are token-authenticated.
/// </summary>
public sealed class DiscordRestChannelClient : IDiscordRestChannelClient
{
    private const string WebhookBaseUrl = "https://discord.com/api/v10";
    private readonly HttpClient _http;
    private readonly ILogger<DiscordRestChannelClient> _logger;

    public DiscordRestChannelClient(HttpClient httpClient, ILogger<DiscordRestChannelClient> logger)
    {
        _http = httpClient;
        _logger = logger;
    }

    // ── Channel list ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DiscordChannelInfo>> ListChannelsAsync(
        string botToken,
        ulong discordGuildId,
        CancellationToken ct = default)
    {
        using var client = await CreateRestClientAsync(botToken, ct).ConfigureAwait(false);

        IReadOnlyCollection<RestGuildChannel> channels;
        try
        {
            var guild = await client.GetGuildAsync(discordGuildId).ConfigureAwait(false)
                ?? throw new DiscordPermissionException(
                    $"Bot is not a member of guild {discordGuildId} (or guild does not exist).");
            channels = await guild.GetChannelsAsync().ConfigureAwait(false);
        }
        catch (Discord.Net.HttpException ex) when ((int)ex.HttpCode == 429)
        {
            throw new DiscordRateLimitException(
                retryAfterSeconds: ParseRetryAfter(ex),
                message: $"Discord rate-limited when listing channels for guild {discordGuildId}");
        }
        catch (Discord.Net.HttpException ex) when ((int)ex.HttpCode is 401 or 403)
        {
            throw new DiscordPermissionException(
                $"Bot lacks permission to list channels for guild {discordGuildId}: {ex.Message}");
        }

        return channels
            .Select(c => new DiscordChannelInfo(
                Id: c.Id,
                Name: c.Name,
                Type: (short)(int)c.GetChannelType().GetValueOrDefault(),
                Position: c is INestedChannel nested ? nested.Position : 0))
            .ToList();
    }

    // ── Message history ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DiscordMessageInfo>> GetMessagesAsync(
        string botToken,
        ulong discordChannelId,
        ulong? beforeSnowflake,
        int limit,
        CancellationToken ct = default)
    {
        using var client = await CreateRestClientAsync(botToken, ct).ConfigureAwait(false);

        var channel = await client.GetChannelAsync(discordChannelId).ConfigureAwait(false)
            as IMessageChannel;
        if (channel is null)
        {
            _logger.LogWarning("Channel {ChannelId} is not a message channel — skipping", discordChannelId);
            return Array.Empty<DiscordMessageInfo>();
        }

        IReadOnlyCollection<IMessage> messages;
        try
        {
            var flat = beforeSnowflake is { } cursor
                ? await channel.GetMessagesAsync(cursor, Direction.Before, limit)
                    .FlattenAsync().ConfigureAwait(false)
                : await channel.GetMessagesAsync(limit)
                    .FlattenAsync().ConfigureAwait(false);
            messages = flat as IReadOnlyCollection<IMessage> ?? flat.ToList();
        }
        catch (Discord.Net.HttpException ex) when ((int)ex.HttpCode == 429)
        {
            throw new DiscordRateLimitException(
                retryAfterSeconds: ParseRetryAfter(ex),
                message: $"Discord rate-limited when reading messages for channel {discordChannelId}");
        }
        catch (Discord.Net.HttpException ex) when ((int)ex.HttpCode is 401 or 403)
        {
            throw new DiscordPermissionException(
                $"Bot lacks permission to read messages for channel {discordChannelId}: {ex.Message}");
        }

        return messages.Select(m => new DiscordMessageInfo(
            Id: m.Id,
            AuthorId: m.Author.Id,
            AuthorName: m.Author.Username,
            AuthorIsWebhook: m.Author is IWebhookUser,
            Content: m.Content ?? "",
            SentAt: m.Timestamp,
            EditedAt: m.EditedTimestamp)).ToList();
    }

    // ── Webhook create ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DiscordWebhookCreated> CreateWebhookAsync(
        string botToken,
        ulong discordChannelId,
        string name,
        CancellationToken ct = default)
    {
        using var client = await CreateRestClientAsync(botToken, ct).ConfigureAwait(false);

        var channel = await client.GetChannelAsync(discordChannelId).ConfigureAwait(false)
            as ITextChannel
            ?? throw new DiscordPermissionException(
                $"Channel {discordChannelId} is not a text channel — cannot create webhook.");

        IWebhook webhook;
        try
        {
            webhook = await channel.CreateWebhookAsync(name).ConfigureAwait(false);
        }
        catch (Discord.Net.HttpException ex) when ((int)ex.HttpCode == 429)
        {
            throw new DiscordRateLimitException(ParseRetryAfter(ex),
                $"Discord rate-limited when creating webhook in channel {discordChannelId}");
        }
        catch (Discord.Net.HttpException ex) when ((int)ex.HttpCode is 401 or 403)
        {
            throw new DiscordPermissionException(
                $"Bot lacks MANAGE_WEBHOOKS on channel {discordChannelId}: {ex.Message}");
        }

        if (string.IsNullOrEmpty(webhook.Token))
            throw new InvalidOperationException(
                $"Discord did not return a token for webhook {webhook.Id}. " +
                "The bot may lack permissions to view the webhook token.");

        return new DiscordWebhookCreated(webhook.Id, webhook.Token);
    }

    // ── Webhook delete ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<bool> DeleteWebhookAsync(
        ulong webhookId,
        string webhookToken,
        CancellationToken ct = default)
    {
        // DELETE /webhooks/{id}/{token} — no bot auth required
        var url = $"{WebhookBaseUrl}/webhooks/{webhookId}/{webhookToken}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false; // already gone — treat as success for idempotent cleanup

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta is { } d
                ? (int)Math.Ceiling(d.TotalSeconds)
                : 5;
            throw new DiscordRateLimitException(retryAfter,
                $"Discord rate-limited when deleting webhook {webhookId}");
        }

        if (!response.IsSuccessStatusCode)
            throw new DiscordPermissionException(
                $"Discord returned {(int)response.StatusCode} when deleting webhook {webhookId}");

        return true;
    }

    // ── Webhook execute ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DiscordMessageInfo> ExecuteWebhookAsync(
        ulong webhookId,
        string webhookToken,
        string username,
        string content,
        CancellationToken ct = default)
    {
        // POST /webhooks/{id}/{token}?wait=true
        var url = $"{WebhookBaseUrl}/webhooks/{webhookId}/{webhookToken}?wait=true";
        var body = new { username, content };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonSerializerOptions.Default),
        };

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new WebhookGoneException(webhookId);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta is { } d
                ? (int)Math.Ceiling(d.TotalSeconds)
                : 5;
            throw new DiscordRateLimitException(retryAfter,
                $"Discord rate-limited when executing webhook {webhookId}");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new DiscordPermissionException(
                $"Discord returned {(int)response.StatusCode} for webhook {webhookId}");

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var msg = await JsonSerializer
            .DeserializeAsync<WebhookMessageResponse>(stream, cancellationToken: ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Discord returned null message body for webhook execute.");

        return new DiscordMessageInfo(
            Id: ulong.Parse(msg.Id),
            AuthorId: msg.Author?.Id is { } authorId ? ulong.Parse(authorId) : webhookId,
            AuthorName: msg.Author?.Username ?? "webhook",
            AuthorIsWebhook: true,
            Content: msg.Content ?? content,
            SentAt: DateTimeOffset.Parse(msg.Timestamp),
            EditedAt: null);
    }

    // ── Webhook message edit ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DiscordMessageInfo> EditWebhookMessageAsync(
        ulong webhookId,
        string webhookToken,
        ulong messageId,
        string newContent,
        CancellationToken ct = default)
    {
        // PATCH /webhooks/{id}/{token}/messages/{message_id}
        var url = $"{WebhookBaseUrl}/webhooks/{webhookId}/{webhookToken}/messages/{messageId}";
        var body = new { content = newContent };

        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: JsonSerializerOptions.Default),
        };

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new WebhookGoneException(webhookId);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta is { } d
                ? (int)Math.Ceiling(d.TotalSeconds)
                : 5;
            throw new DiscordRateLimitException(retryAfter,
                $"Discord rate-limited when editing webhook message {messageId}");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new DiscordPermissionException(
                $"Discord returned {(int)response.StatusCode} when editing webhook message {messageId}");

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var msg = await JsonSerializer
            .DeserializeAsync<WebhookMessageResponse>(stream, cancellationToken: ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Discord returned null message body for webhook edit.");

        return new DiscordMessageInfo(
            Id: ulong.Parse(msg.Id),
            AuthorId: msg.Author?.Id is { } authorId ? ulong.Parse(authorId) : webhookId,
            AuthorName: msg.Author?.Username ?? "webhook",
            AuthorIsWebhook: true,
            Content: msg.Content ?? newContent,
            SentAt: DateTimeOffset.Parse(msg.Timestamp),
            // Discord returns edited_timestamp on edits — we treat "now" as the
            // edited time since the JSON model above doesn't track it. Callers
            // that need precise edited_at use Discord's MessageUpdated gateway
            // event instead (which carries the authoritative timestamp).
            EditedAt: DateTimeOffset.UtcNow);
    }

    // ── Webhook message delete ────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<bool> DeleteWebhookMessageAsync(
        ulong webhookId,
        string webhookToken,
        ulong messageId,
        CancellationToken ct = default)
    {
        // DELETE /webhooks/{id}/{token}/messages/{message_id}
        var url = $"{WebhookBaseUrl}/webhooks/{webhookId}/{webhookToken}/messages/{messageId}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false; // already gone — idempotent success

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta is { } d
                ? (int)Math.Ceiling(d.TotalSeconds)
                : 5;
            throw new DiscordRateLimitException(retryAfter,
                $"Discord rate-limited when deleting webhook message {messageId}");
        }

        if (!response.IsSuccessStatusCode)
            throw new DiscordPermissionException(
                $"Discord returned {(int)response.StatusCode} when deleting webhook message {messageId}");

        return true;
    }

    // ── Bot channel-message delete (Task 5: full implementation) ─────────────

    /// <inheritdoc/>
    /// <remarks>Full implementation is wired in Task 5 (DiscordNetBotConnection context).
    /// This stub allows Task 4 MessageService to compile; at runtime the real implementation
    /// will be provided before this path can be exercised.</remarks>
    public Task<bool> DeleteChannelMessageAsync(
        ulong discordChannelId,
        ulong discordMessageId,
        long guildId,
        CancellationToken ct = default)
        => throw new NotImplementedException(
            "DeleteChannelMessageAsync is implemented in Task 5. This path requires a wired bot connection.");

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<DiscordRestClient> CreateRestClientAsync(
        string botToken, CancellationToken ct)
    {
        var client = new DiscordRestClient();
        await client.LoginAsync(TokenType.Bot, botToken).ConfigureAwait(false);
        return client;
    }

    private static int ParseRetryAfter(Discord.Net.HttpException ex)
    {
        // Discord.Net exposes RetryAfter as a double (seconds). Attempt reflection-based read;
        // fall back to 5 seconds if property not present on this version.
        try
        {
            var prop = ex.GetType().GetProperty("RetryAfter")
                    ?? ex.GetType().GetProperty("RetryAfterMs");
            if (prop?.GetValue(ex) is double d) return (int)Math.Ceiling(d);
            if (prop?.GetValue(ex) is int i) return i;
        }
        catch { /* reflection failed — fall through */ }

        return 5;
    }

    // ── JSON response types for webhook execute ───────────────────────────────

    private sealed class WebhookMessageResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string? Content { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("author")]
        public WebhookAuthorResponse? Author { get; set; }
    }

    private sealed class WebhookAuthorResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("username")]
        public string? Username { get; set; }
    }
}
