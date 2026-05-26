namespace DwbHub.Api.Controllers.Tenant;

/// <summary>Per-guild item returned by guild list and guild mutation endpoints.</summary>
/// <param name="PublicId">Public GUID that identifies the guild in API paths.</param>
/// <param name="DiscordGuildId">Discord snowflake ID as a string.</param>
/// <param name="DisplayName">Human-readable guild name.</param>
/// <param name="IsActive">Whether the guild is currently active (bot may connect).</param>
/// <param name="RegisteredAt">Timestamp when the guild was registered with DwbHub.</param>
/// <param name="BotCredentialsConfigured">Whether an encrypted bot token has been stored for this guild.</param>
/// <param name="BotConnectionState">
/// Current gateway connection state string (<c>disconnected</c>, <c>connecting</c>,
/// <c>connected</c>, <c>token_invalid</c>, <c>failed</c>), or <c>null</c> when
/// credentials are not yet configured.
/// </param>
public sealed record GuildResponse(
    Guid PublicId,
    string DiscordGuildId,
    string DisplayName,
    bool IsActive,
    DateTimeOffset RegisteredAt,
    bool BotCredentialsConfigured,
    string? BotConnectionState);
