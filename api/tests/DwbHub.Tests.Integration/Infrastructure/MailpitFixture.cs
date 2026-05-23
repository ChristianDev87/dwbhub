using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace DwbHub.Tests.Integration.Infrastructure;

/// <summary>
/// One Mailpit container per test session. SMTP on 1025; REST API on 8025.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    // Plan 0.8.1: explicit host port bindings so that docker-in-docker runners on custom
    // bridge networks (test-net) can reach this container via the docker0 gateway
    // (172.17.0.1). Docker Desktop for Windows only routes explicitly-mapped ports
    // through the bridge iptables rules; random ephemeral ports are not reachable
    // cross-bridge.
    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("axllent/mailpit:v1.21")
        .WithPortBinding(11025, 1025)
        .WithPortBinding(18025, 8025)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8025))
        .Build();

    public string SmtpHost => _container.Hostname;
    public int SmtpPort => _container.GetMappedPublicPort(1025);
    public int ApiPort => _container.GetMappedPublicPort(8025);
    public string ApiBaseUrl => $"http://{_container.Hostname}:{ApiPort}";

    public async Task InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);
    public async Task DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    public async Task<MailpitMessage[]> GetMessagesAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ApiBaseUrl) };
        var resp = await http.GetFromJsonAsync<MailpitMessagesResponse>("/api/v1/messages", ct).ConfigureAwait(false);
        return resp?.Messages ?? Array.Empty<MailpitMessage>();
    }

    public async Task<MailpitMessageDetail> GetMessageDetailAsync(string id, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ApiBaseUrl) };
        return await http.GetFromJsonAsync<MailpitMessageDetail>($"/api/v1/message/{id}", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {id} not found");
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ApiBaseUrl) };
        var resp = await http.DeleteAsync("/api/v1/messages", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
}

public sealed record MailpitMessagesResponse(MailpitMessage[] Messages, int Total);
public sealed record MailpitMessage(string ID, string Subject, MailpitAddress[] To, DateTimeOffset Created);
public sealed record MailpitAddress(string Address, string? Name);
public sealed record MailpitMessageDetail(string ID, string Subject, string HTML, string Text);
