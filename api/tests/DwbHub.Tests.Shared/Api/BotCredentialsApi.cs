using System.Globalization;
using System.Net.Http.Json;

namespace DwbHub.Tests.Shared.Api;

/// <summary>
/// HttpClient extensions for bot-credentials endpoints.
/// Keeps test code free of hand-typed URL strings.
/// </summary>
public static class BotCredentialsApi
{
    public static Task<HttpResponseMessage> PutBotCredentialsAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId, object body) =>
        client.PutAsJsonAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/t/{tenantSlug}/guilds/{guildPublicId:D}/bot-credentials"),
            body);

    public static Task<HttpResponseMessage> DeleteBotCredentialsAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId) =>
        client.DeleteAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"/api/t/{tenantSlug}/guilds/{guildPublicId:D}/bot-credentials"));
}
