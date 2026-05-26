using DwbHub.Application.Bot;
using DwbHub.Application.Messaging;

namespace DwbHub.Tests.Integration.Bot;

/// <summary>
/// Test-controlled implementation of IBotConnection. Production code never sees this.
/// Tests drive state changes via TriggerXxx methods + read recorded calls via fields.
/// </summary>
public sealed class FakeBotConnection : IBotConnection
{
    public long GuildId { get; }
    public long TenantId { get; }
    public BotConnectionState State { get; private set; } = BotConnectionState.Disconnected;
    public DateTimeOffset? LastConnectedAt { get; private set; }

    public event Func<BotConnectionStateChange, Task>? StateChanged;
    public event Func<MessageReceivedEvent, Task>? MessageReceived;
    public event Func<MessageUpdatedEvent, Task>? MessageUpdated;
    public event Func<MessageDeletedEvent, Task>? MessageDeleted;

    // Test-recording fields
    public List<string> ConnectCallsWithTokens { get; } = new();
    public int DisconnectCalls { get; private set; }
    public bool DisposedAsync { get; private set; }

    // Test-driven outcome: by default Connect transitions to Connected.
    // Tests can override via SetConnectOutcome before calling ConnectAsync.
    public BotConnectionState ConnectOutcome { get; set; } = BotConnectionState.Connected;
    public string? ConnectOutcomeError { get; set; }

    public FakeBotConnection(long guildId, long tenantId)
    {
        GuildId = guildId;
        TenantId = tenantId;
    }

    public async Task ConnectAsync(string plaintextToken, CancellationToken ct)
    {
        ConnectCallsWithTokens.Add(plaintextToken);
        await TransitionAsync(BotConnectionState.Connecting, null);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        await TransitionAsync(ConnectOutcome, ConnectOutcomeError);
        if (ConnectOutcome == BotConnectionState.Connected)
            LastConnectedAt = DateTimeOffset.UtcNow;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        DisconnectCalls++;
        if (State == BotConnectionState.Disconnected) return;
        await TransitionAsync(BotConnectionState.Disconnected, null);
    }

    public async ValueTask DisposeAsync()
    {
        DisposedAsync = true;
        await Task.CompletedTask;
    }

    // Test-controlled trigger for unsolicited state changes (e.g. Discord side disconnect).
    public Task TriggerStateAsync(BotConnectionState newState, string? errorClass = null)
        => TransitionAsync(newState, errorClass);

    // Test-only Raise* helpers so tests can synthesise inbound message events.
    public Task RaiseMessageReceivedAsync(MessageReceivedEvent evt) =>
        MessageReceived?.Invoke(evt) ?? Task.CompletedTask;

    public Task RaiseMessageUpdatedAsync(MessageUpdatedEvent evt) =>
        MessageUpdated?.Invoke(evt) ?? Task.CompletedTask;

    public Task RaiseMessageDeletedAsync(MessageDeletedEvent evt) =>
        MessageDeleted?.Invoke(evt) ?? Task.CompletedTask;

    private async Task TransitionAsync(BotConnectionState newState, string? errorClass)
    {
        if (newState == State) return;
        var change = new BotConnectionStateChange(
            From: State, To: newState, ChangedAt: DateTimeOffset.UtcNow, ErrorClass: errorClass);
        State = newState;
        if (StateChanged is { } handler)
            await handler(change).ConfigureAwait(false);
    }
}
