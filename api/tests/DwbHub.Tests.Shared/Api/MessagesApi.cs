using System.Globalization;
using System.Net.Http.Json;

namespace DwbHub.Tests.Shared.Api;

/// <summary>
/// HttpClient extensions for messages endpoints.
/// Keeps test code free of hand-typed URL strings.
/// </summary>
public static class MessagesApi
{
    public static Task<HttpResponseMessage> PostMessageAsync(
        this HttpClient client, string tenantSlug, Guid channelPublicId, object body) =>
        client.PostAsJsonAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/t/{tenantSlug}/channels/{channelPublicId:D}/messages"),
            body);

    public static Task<HttpResponseMessage> GetMessagesAsync(
        this HttpClient client, string tenantSlug, Guid channelPublicId, int? limit = null)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/t/{tenantSlug}/channels/{channelPublicId:D}/messages");

        if (limit is not null)
            url = string.Create(CultureInfo.InvariantCulture, $"{url}?limit={limit}");

        return client.GetAsync(url);
    }

    public static Task<HttpResponseMessage> PatchMessageAsync(
        this HttpClient client, string tenantSlug, Guid channelPublicId, Guid messagePublicId, object body) =>
        client.PatchAsJsonAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/t/{tenantSlug}/channels/{channelPublicId:D}/messages/{messagePublicId:D}"),
            body);

    public static Task<HttpResponseMessage> DeleteMessageAsync(
        this HttpClient client, string tenantSlug, Guid channelPublicId, Guid messagePublicId) =>
        client.DeleteAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/t/{tenantSlug}/channels/{channelPublicId:D}/messages/{messagePublicId:D}"));
}
