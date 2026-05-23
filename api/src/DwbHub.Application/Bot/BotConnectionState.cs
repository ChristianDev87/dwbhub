namespace DwbHub.Application.Bot;

/// <summary>
/// Lifecycle states of a per-guild Discord bot connection.
/// Production state lives in-memory in the singleton BotConnectionManager;
/// transitions emit audit events and may update guilds.last_connected_at.
/// </summary>
public enum BotConnectionState
{
    /// <summary>Initial state, or after explicit disconnect / deactivation.</summary>
    Disconnected,

    /// <summary>Connect-call in flight (LoginAsync / StartAsync awaiting Ready).</summary>
    Connecting,

    /// <summary>Discord gateway READY received; bot is online.</summary>
    Connected,

    /// <summary>Discord returned 401 Unauthorized — operator must rotate token.
    /// Manager does NOT auto-reconnect from this state (would waste rate limit).</summary>
    TokenInvalid,

    /// <summary>Persistent error other than token (network, gateway 5xx, etc.).
    /// Discord.NET's built-in retry handles transient errors inside Connecting.</summary>
    Failed,
}
