using DwbHub.Application.Messaging;

namespace DwbHub.Tests.Integration.Messaging;

/// <summary>
/// Test-only <see cref="IDiscordRestChannelClient"/> that records every mutating call
/// so integration tests can assert what was sent to Discord.
///
/// All read operations return minimal valid responses.
/// EditWebhook returns success; DeleteWebhook/DeleteChannel return true.
/// </summary>
public sealed class CapturingDiscordFake : IDiscordRestChannelClient
{
    public List<(ulong WebhookId, ulong MessageId, string NewContent)> EditWebhookCalls { get; } = [];
    public List<(ulong WebhookId, ulong MessageId)> DeleteWebhookCalls { get; } = [];
    public List<(ulong ChannelId, ulong MessageId, long GuildId)> DeleteChannelCalls { get; } = [];

    // DeleteChannelMessage can be configured to return false (simulate missing permission).
    public bool DeleteChannelMessageResult { get; set; } = true;

    public Task<IReadOnlyList<DiscordChannelInfo>> ListChannelsAsync(
        string botToken, ulong discordGuildId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiscordChannelInfo>>(Array.Empty<DiscordChannelInfo>());

    public Task<IReadOnlyList<DiscordMessageInfo>> GetMessagesAsync(
        string botToken, ulong discordChannelId, ulong? beforeSnowflake, int limit,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiscordMessageInfo>>(Array.Empty<DiscordMessageInfo>());

    public Task<DiscordWebhookCreated> CreateWebhookAsync(
        string botToken, ulong discordChannelId, string name, CancellationToken ct = default)
        => Task.FromResult(new DiscordWebhookCreated(900000000000099999UL, "test-token"));

    public Task<bool> DeleteWebhookAsync(
        ulong webhookId, string webhookToken, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<DiscordMessageInfo> ExecuteWebhookAsync(
        ulong webhookId, string webhookToken, string username, string content,
        CancellationToken ct = default)
    {
        var fakeId = 800000000000099000UL + (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 99999);
        return Task.FromResult(new DiscordMessageInfo(
            Id: fakeId, AuthorId: 1UL, AuthorName: username, AuthorIsWebhook: true,
            Content: content, SentAt: DateTimeOffset.UtcNow, EditedAt: null));
    }

    public Task<DiscordMessageInfo> EditWebhookMessageAsync(
        ulong webhookId, string webhookToken, ulong messageId, string newContent,
        CancellationToken ct = default)
    {
        EditWebhookCalls.Add((webhookId, messageId, newContent));
        return Task.FromResult(new DiscordMessageInfo(
            Id: messageId, AuthorId: 1UL, AuthorName: "webhook", AuthorIsWebhook: true,
            Content: newContent, SentAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            EditedAt: DateTimeOffset.UtcNow));
    }

    public Task<bool> DeleteWebhookMessageAsync(
        ulong webhookId, string webhookToken, ulong messageId, CancellationToken ct = default)
    {
        DeleteWebhookCalls.Add((webhookId, messageId));
        return Task.FromResult(true);
    }

    public Task<bool> DeleteChannelMessageAsync(
        ulong discordChannelId, ulong discordMessageId, long guildId, CancellationToken ct = default)
    {
        if (DeleteChannelMessageResult)
            DeleteChannelCalls.Add((discordChannelId, discordMessageId, guildId));
        return Task.FromResult(DeleteChannelMessageResult);
    }
}
