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
}
