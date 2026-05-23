namespace DwbHub.Application.Bot;

/// <summary>
/// Emitted on every external state transition. Safe to log + put in audit payload —
/// the ErrorClass field is sanitised to a short identifier (e.g. "network_timeout",
/// "gateway_unauthorized") and NEVER contains the plaintext bot token or full
/// exception stack-trace.
/// </summary>
public sealed record BotConnectionStateChange(
    BotConnectionState From,
    BotConnectionState To,
    DateTimeOffset ChangedAt,
    string? ErrorClass = null);
