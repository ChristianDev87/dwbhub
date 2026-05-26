namespace DwbHub.Application.Bot;

/// <summary>
/// Emitted on every external state transition. Safe to log + put in audit payload —
/// the ErrorClass field is sanitised to a short identifier (e.g. "network_timeout",
/// "gateway_unauthorized") and never contains the plaintext bot token or full
/// exception stack-trace.
/// </summary>
/// <param name="From">State before the transition.</param>
/// <param name="To">State after the transition.</param>
/// <param name="ChangedAt">UTC timestamp of the transition.</param>
/// <param name="ErrorClass">Short sanitised error identifier, or <c>null</c> on non-error transitions.</param>
public sealed record BotConnectionStateChange(
    BotConnectionState From,
    BotConnectionState To,
    DateTimeOffset ChangedAt,
    string? ErrorClass = null);
