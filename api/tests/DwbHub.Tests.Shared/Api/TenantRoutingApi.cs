using System.Globalization;

namespace DwbHub.Tests.Shared.Api;

/// <summary>
/// HttpClient extensions for tenant-routing test endpoints.
/// Keeps test code free of hand-typed URL strings.
/// </summary>
public static class TenantRoutingApi
{
    public static Task<HttpResponseMessage> GetMeAsync(
        this HttpClient client, string tenantSlug) =>
        client.GetAsync(string.Create(
            CultureInfo.InvariantCulture, $"/api/t/{tenantSlug}/me"));

    public static Task<HttpResponseMessage> GetDashboardAsync(
        this HttpClient client, string tenantSlug) =>
        client.GetAsync(string.Create(
            CultureInfo.InvariantCulture, $"/api/t/{tenantSlug}/dashboard"));

    public static Task<HttpResponseMessage> GetGuildAnythingAsync(
        this HttpClient client, string tenantSlug, Guid guildPublicId) =>
        client.GetAsync(string.Create(
            CultureInfo.InvariantCulture, $"/api/t/{tenantSlug}/g/{guildPublicId:D}/anything"));
}
