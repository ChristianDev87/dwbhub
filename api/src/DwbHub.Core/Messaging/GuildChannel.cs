namespace DwbHub.Core.Messaging;

/// <summary>
/// A Discord channel known to the system for a bridged guild.
/// `PublicId` is the UUID that appears in URLs and API responses; never expose
/// the internal `Id` in HTTP responses.
/// `IsBridged` is owner-managed: only bridged channels receive message persistence
/// and backfill (Plan 1.0 §2).
/// </summary>
public sealed record GuildChannel
{
    public long Id { get; init; }
    public long TenantId { get; init; }
    public long GuildId { get; init; }                    // internal guilds.id
    public Guid PublicId { get; init; }                   // URL-safe identifier
    public long DiscordChannelId { get; init; }
    public string Name { get; init; } = "";
    public short ChannelType { get; init; }               // 0=text, 5=announcement, etc.
    public int Position { get; init; }
    public bool IsBridged { get; init; }
    public DateTimeOffset? BridgedAt { get; init; }
    public DateTimeOffset LastSyncedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
