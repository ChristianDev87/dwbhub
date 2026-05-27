using System.Globalization;

namespace DwbHub.Tests.Shared.Api;

/// <summary>
/// HttpClient extensions for channel endpoints (both guild-scoped and channel-scoped).
/// Keeps test code free of hand-typed URL strings.
/// </summary>
public static class ChannelsApi
{
    // ── Guild-scoped ──────────────────────────────────────────────────────────

    public static Task<HttpResponseMessage> ListChannelsAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId) =>
        client.GetAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"/api/t/{tenantSlug}/guilds/{guildPublicId:D}/channels"));

    public static Task<HttpResponseMessage> SyncChannelsAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId) =>
        client.PostAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/t/{tenantSlug}/guilds/{guildPublicId:D}/channels/sync"),
            content: null);

    // ── Channel-scoped ────────────────────────────────────────────────────────

    public static Task<HttpResponseMessage> BridgeChannelAsync(
        this HttpClient client, string tenantSlug, Guid channelPublicId) =>
        client.PostAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/t/{tenantSlug}/channels/{channelPublicId:D}/bridge"),
            content: null);

    public static Task<HttpResponseMessage> UnbridgeChannelAsync(
        this HttpClient client, string tenantSlug, Guid channelPublicId) =>
        client.DeleteAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"/api/t/{tenantSlug}/channels/{channelPublicId:D}/bridge"));

    public static Task<HttpResponseMessage> GetBackfillStatusAsync(
        this HttpClient client, string tenantSlug, Guid channelPublicId) =>
        client.GetAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"/api/t/{tenantSlug}/channels/{channelPublicId:D}/backfill-status"));

    public static Task<HttpResponseMessage> CancelBackfillJobAsync(
        this HttpClient client, string tenantSlug, Guid channelPublicId, long jobId) =>
        client.DeleteAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"/api/t/{tenantSlug}/channels/{channelPublicId:D}/backfill-jobs/{jobId}"));
}
