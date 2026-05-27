using System.Globalization;

namespace DwbHub.Tests.Shared.Api;

/// <summary>
/// HttpClient extensions for the guild-bot lifecycle endpoints
/// under /api/t/{slug}/guilds/{publicId}/...
/// Keeps test code free of hand-typed URL strings.
/// </summary>
public static class GuildBotApi
{
    public static Task<HttpResponseMessage> ActivateGuildAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId) =>
        client.PostAsync(GuildRoute(tenantSlug, guildPublicId, "activate"), content: null);

    public static Task<HttpResponseMessage> DeactivateGuildAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId) =>
        client.PostAsync(GuildRoute(tenantSlug, guildPublicId, "deactivate"), content: null);

    public static Task<HttpResponseMessage> ReconnectBotAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId) =>
        client.PostAsync(GuildRoute(tenantSlug, guildPublicId, "bot/reconnect"), content: null);

    private static string GuildRoute(string tenantSlug, Guid guildPublicId, string action) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"/api/t/{tenantSlug}/guilds/{guildPublicId:D}/{action}");
}
