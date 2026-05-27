namespace DwbHub.Api.Controllers.Tenant;

/// <summary>Response body for GET /api/t/{slug}/channels/{channelPublicId}/messages.</summary>
/// <param name="Messages">Messages on this page, ordered newest-first.</param>
/// <param name="NextBefore">
/// Discord snowflake to pass as <c>?before=</c> to retrieve the next (older) page,
/// or <c>null</c> when the end of the channel history has been reached.
/// </param>
public sealed record MessageHistoryResponse(
    IReadOnlyList<MessageHistoryItem> Messages,
    long? NextBefore);

/// <summary>A single message item in the history response.</summary>
/// <param name="Id">Internal database ID of the message row.</param>
/// <param name="PublicId">External UUID identifier of the message. Required for PATCH/DELETE requests.</param>
/// <param name="AuthorName">Discord display name of the author at send time.</param>
/// <param name="Content">Message text content.</param>
/// <param name="SentAt">Timestamp when the message was originally sent on Discord.</param>
/// <param name="EditedAt">Timestamp of the last edit, or <c>null</c> if never edited.</param>
/// <param name="ViaDwbhub"><c>true</c> when the message was sent through the DwbHub webhook.</param>
/// <param name="DiscordMessageId">Discord snowflake identifying this message.</param>
public sealed record MessageHistoryItem(
    long Id,
    Guid PublicId,
    string AuthorName,
    string Content,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    bool ViaDwbhub,
    long DiscordMessageId);
