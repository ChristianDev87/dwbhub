using System.Globalization;

namespace DwbHub.Tests.Shared.Api;

/// <summary>
/// HttpClient extensions for global (non-tenant) endpoints.
/// Keeps test code free of hand-typed URL strings.
/// </summary>
public static class GlobalApi
{
    public static Task<HttpResponseMessage> GetHealthAsync(this HttpClient client) =>
        client.GetAsync("/api/health");

    public static Task<HttpResponseMessage> GetSetupStatusAsync(this HttpClient client) =>
        client.GetAsync("/api/setup/status");

    public static Task<HttpResponseMessage> PostAuthRefreshAsync(this HttpClient client) =>
        client.PostAsync("/api/auth/refresh", content: null);
}
