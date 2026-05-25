namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Response body for GET /api/t/{slug}/channels/{channelPublicId}/messages.
/// </summary>
public sealed record MessageHistoryResponse(
    IReadOnlyList<MessageHistoryItem> Messages,
    long? NextBefore);

/// <summary>
/// A single message item returned in the history response.
/// </summary>
public sealed record MessageHistoryItem(
    long Id,
    string AuthorName,
    string Content,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    bool ViaDwbhub,
    long DiscordMessageId);
