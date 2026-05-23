using System.Collections.Concurrent;
using DwbHub.Application.Bot;

namespace DwbHub.Tests.Integration.Bot;

/// <summary>
/// Returns FakeBotConnection instances and exposes the created instances by guildId
/// so tests can assert on lifecycle. Thread-safe for parallel test scenarios.
/// </summary>
public sealed class FakeBotConnectionFactory : IBotConnectionFactory
{
    private readonly ConcurrentDictionary<long, FakeBotConnection> _created = new();

    public IReadOnlyDictionary<long, FakeBotConnection> Created => _created;

    public IBotConnection Create(long guildId, long tenantId)
    {
        var fake = new FakeBotConnection(guildId, tenantId);
        _created[guildId] = fake;
        return fake;
    }

    /// <summary>
    /// Polls until a connection has been created for <paramref name="guildId"/> AND
    /// it has recorded at least one ConnectAsync call, or the timeout elapses.
    /// Returns the connection on success, throws TimeoutException on failure.
    /// Replaces wall-clock Task.Delay() patterns in fire-and-forget assertions.
    /// </summary>
    public async Task<FakeBotConnection> WaitForConnectAsync(long guildId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_created.TryGetValue(guildId, out var conn) && conn.ConnectCallsWithTokens.Count > 0)
                return conn;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"Timed out waiting for connection on guild {guildId} after {timeout}.");
    }
}
