using DwbHub.Application.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwbHub.Infrastructure.Messaging;

/// <summary>
/// Scripted fake for <see cref="IDiscordRestChannelClient"/> used in e2e test runs.
///
/// Activated when DWBHUB_DISCORD_TEST_MODE=fake-rest is set on the API container.
/// Registered in Program.cs instead of the real DiscordRestChannelClient when
/// that env-var is present (non-Production guard in Program.cs AND in this ctor).
///
/// PRODUCTION SAFETY: this class throws at construction time if the host
/// environment is Production. Defense-in-depth: if the env-var check in
/// Program.cs is ever bypassed, the runtime startup explodes loudly.
/// </summary>
public sealed class FakeDiscordRestChannelClient : IDiscordRestChannelClient
{
    // ── Deterministic fake Discord IDs ────────────────────────────────────────
    // All IDs are 18-digit decimals — valid per the guilds CHECK constraint
    // that requires discord_id between 10^16 and 10^20-1.

    // Channels returned by ListChannelsAsync
    private static readonly IReadOnlyList<DiscordChannelInfo> FakeChannels =
    [
        new(Id: 100000000000000001UL, Name: "general",    Type: 0, Position: 0),
        new(Id: 100000000000000002UL, Name: "announcements", Type: 0, Position: 1),
        new(Id: 100000000000000003UL, Name: "dev-chat",   Type: 0, Position: 2),
        new(Id: 100000000000000004UL, Name: "voice-lobby",Type: 2, Position: 3),
    ];

    // ── Backfill message pages (newest-first, as Discord returns them) ─────────
    // Page 1: 50 messages with snowflakes  200000000000000001 … 200000000000000050
    // Page 2: 50 messages with snowflakes  200000000000000051 … 200000000000000100
    // Page 3: 100 messages with snowflakes 200000000000000101 … 200000000000000200
    // Page 4+: empty → backfill terminates

    private static readonly IReadOnlyList<DiscordMessageInfo> Page1Messages =
        BuildPage(startOrdinal: 50, count: 50);   // ordinal 50..1 (newest-first)

    private static readonly IReadOnlyList<DiscordMessageInfo> Page2Messages =
        BuildPage(startOrdinal: 100, count: 50);  // ordinal 100..51

    private static readonly IReadOnlyList<DiscordMessageInfo> Page3Messages =
        BuildPage(startOrdinal: 200, count: 100); // ordinal 200..101

    private static IReadOnlyList<DiscordMessageInfo> BuildPage(int startOrdinal, int count)
    {
        // startOrdinal is the highest ordinal on the page; we count down for newest-first
        var result = new List<DiscordMessageInfo>(count);
        for (int i = 0; i < count; i++)
        {
            ulong ordinal = (ulong)(startOrdinal - i);
            ulong snowflake = 200000000000000000UL + ordinal;
            result.Add(new DiscordMessageInfo(
                Id: snowflake,
                AuthorId: 300000000000000001UL,
                AuthorName: "fake-user",
                AuthorIsWebhook: false,
                Content: $"Fake message {ordinal} from backfill",
                SentAt: DateTimeOffset.UtcNow.AddMinutes(-(int)ordinal),
                EditedAt: null));
        }
        return result.AsReadOnly();
    }

    // ── Ctor — production guard ───────────────────────────────────────────────

    public FakeDiscordRestChannelClient(
        IHostEnvironment env,
        ILogger<FakeDiscordRestChannelClient> logger)
    {
        if (env.IsProduction())
            throw new InvalidOperationException(
                "FakeDiscordRestChannelClient must NEVER be activated in Production. " +
                "Check DWBHUB_DISCORD_TEST_MODE env var is unset on the production stack.");

        logger.LogWarning(
            "FakeDiscordRestChannelClient activated (test mode) — NOT for production use");
    }

    // ── IDiscordRestChannelClient ─────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<DiscordChannelInfo>> ListChannelsAsync(
        string botToken,
        ulong discordGuildId,
        CancellationToken ct = default)
        => Task.FromResult(FakeChannels);

    /// <inheritdoc/>
    public Task<IReadOnlyList<DiscordMessageInfo>> GetMessagesAsync(
        string botToken,
        ulong discordChannelId,
        ulong? beforeSnowflake,
        int limit,
        CancellationToken ct = default)
    {
        // Scripted pagination:
        //   call 1: beforeSnowflake == null   → page 1 (50 msgs, oldest = 200000000000000001)
        //   call 2: beforeSnowflake == ...001 → page 2 (50 msgs, oldest = 200000000000000051)
        //   call 3: beforeSnowflake == ...051 → page 3 (100 msgs, oldest = 200000000000000101)
        //   call 4: beforeSnowflake == ...101 → empty → backfill ends

        if (beforeSnowflake is null)
            return Task.FromResult(Page1Messages);

        const ulong OldestPage1 = 200000000000000001UL;
        const ulong OldestPage2 = 200000000000000051UL;
        const ulong OldestPage3 = 200000000000000101UL;

        if (beforeSnowflake == OldestPage1)
            return Task.FromResult(Page2Messages);

        if (beforeSnowflake == OldestPage2)
            return Task.FromResult(Page3Messages);

        if (beforeSnowflake == OldestPage3)
            return Task.FromResult<IReadOnlyList<DiscordMessageInfo>>(
                Array.Empty<DiscordMessageInfo>());

        // Any other cursor → empty (safety net for unexpected states)
        return Task.FromResult<IReadOnlyList<DiscordMessageInfo>>(
            Array.Empty<DiscordMessageInfo>());
    }

    /// <inheritdoc/>
    public Task<DiscordWebhookCreated> CreateWebhookAsync(
        string botToken,
        ulong discordChannelId,
        string name,
        CancellationToken ct = default)
        => Task.FromResult(
            new DiscordWebhookCreated(
                WebhookId: 900000000000000001UL,
                WebhookToken: "fake-webhook-token-do-not-leak"));

    /// <inheritdoc/>
    public Task<bool> DeleteWebhookAsync(
        ulong webhookId,
        string webhookToken,
        CancellationToken ct = default)
        => Task.FromResult(true); // no-op: fake webhooks are never stored externally

    /// <inheritdoc/>
    public Task<DiscordMessageInfo> ExecuteWebhookAsync(
        ulong webhookId,
        string webhookToken,
        string username,
        string content,
        CancellationToken ct = default)
    {
        // Generate a deterministic-ish snowflake from current time
        // (not truly snowflake-format, but unique enough for e2e tests)
        var millis = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ulong fakeId = 800000000000000000UL + (millis % 99999999999UL);

        return Task.FromResult(new DiscordMessageInfo(
            Id: fakeId,
            AuthorId: 900000000000000002UL,
            AuthorName: username,
            AuthorIsWebhook: true,
            Content: content,
            SentAt: DateTimeOffset.UtcNow,
            EditedAt: null));
    }

    /// <inheritdoc/>
    public Task<DiscordMessageInfo> EditWebhookMessageAsync(
        ulong webhookId,
        string webhookToken,
        ulong messageId,
        string newContent,
        CancellationToken ct = default)
        => Task.FromResult(new DiscordMessageInfo(
            Id: messageId,
            AuthorId: 900000000000000002UL,
            AuthorName: "webhook",
            AuthorIsWebhook: true,
            Content: newContent,
            SentAt: DateTimeOffset.UtcNow.AddMinutes(-1), // pretend the message was sent a moment ago
            EditedAt: DateTimeOffset.UtcNow));

    /// <inheritdoc/>
    public Task<bool> DeleteWebhookMessageAsync(
        ulong webhookId,
        string webhookToken,
        ulong messageId,
        CancellationToken ct = default)
        => Task.FromResult(true); // no-op: fake messages are never stored externally

    /// <inheritdoc/>
    public Task<bool> DeleteChannelMessageAsync(
        ulong discordChannelId,
        ulong discordMessageId,
        long guildId,
        CancellationToken ct = default)
        => Task.FromResult(true); // no-op in fake: simulates bot having MANAGE_MESSAGES
}
