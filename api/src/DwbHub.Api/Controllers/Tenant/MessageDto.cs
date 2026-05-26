using DwbHub.Core.Messaging;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Full message representation returned from mutating endpoints (PATCH edit).
/// Shape mirrors <see cref="MessageHistoryItem"/> so the frontend can update its local cache directly.
/// </summary>
/// <param name="Id">Internal database ID of the message row.</param>
/// <param name="AuthorName">Discord display name of the author at send time.</param>
/// <param name="Content">Current message text content.</param>
/// <param name="SentAt">Timestamp when the message was originally sent on Discord.</param>
/// <param name="EditedAt">Timestamp of the last edit, or <c>null</c> if never edited.</param>
/// <param name="ViaDwbhub"><c>true</c> when the message was sent through the DwbHub webhook.</param>
/// <param name="DiscordMessageId">Discord snowflake identifying this message.</param>
public sealed record MessageDto(
    long Id,
    string AuthorName,
    string Content,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    bool ViaDwbhub,
    long DiscordMessageId)
{
    /// <summary>Maps a <see cref="Message"/> entity to a <see cref="MessageDto"/>.</summary>
    public static MessageDto From(Message msg) => new(
        Id: msg.Id,
        AuthorName: msg.DiscordAuthorName,
        Content: msg.Content,
        SentAt: msg.SentAt,
        EditedAt: msg.EditedAt,
        ViaDwbhub: msg.ViaDwbhub,
        DiscordMessageId: msg.DiscordMessageId);
}
