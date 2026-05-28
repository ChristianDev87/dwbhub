using System.Globalization;
using System.Net.Http.Json;

namespace DwbHub.Tests.Shared.Api;

/// <summary>
/// HttpClient extensions for the PATCH /api/t/{slug}/settings endpoint.
/// Keeps test code free of hand-typed URL strings.
/// </summary>
public static class TenantSettingsApi
{
    /// <summary>PATCH /api/t/{tenantSlug}/settings with a JSON body.</summary>
    public static Task<HttpResponseMessage> PatchTenantSettingsAsync(
        this HttpClient client, string tenantSlug, object body) =>
        client.PatchAsJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/t/{tenantSlug}/settings"),
            body);
}
