using System.Globalization;
using System.Net.Http.Json;

namespace DwbHub.Tests.Shared.Api;

/// <summary>
/// HttpClient extensions for guild CRUD endpoints.
/// Keeps test code free of hand-typed URL strings.
/// </summary>
public static class GuildsApi
{
    public static Task<HttpResponseMessage> CreateGuildAsync(
        this HttpClient client, string tenantSlug, object body) =>
        client.PostAsJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/t/{tenantSlug}/guilds"),
            body);

    public static Task<HttpResponseMessage> ListGuildsAsync(
        this HttpClient client, string tenantSlug) =>
        client.GetAsync(string.Create(
            CultureInfo.InvariantCulture, $"/api/t/{tenantSlug}/guilds"));

    public static Task<HttpResponseMessage> DeleteGuildAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId) =>
        client.DeleteAsync(string.Create(
            CultureInfo.InvariantCulture, $"/api/t/{tenantSlug}/guilds/{guildPublicId:D}"));
}
