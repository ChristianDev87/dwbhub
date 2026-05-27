namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Response body for a successfully edited message (200 OK).
/// </summary>
/// <param name="Id">Internal database ID of the message.</param>
/// <param name="PublicId">External UUID identifier of the message.</param>
/// <param name="AuthorName">Display name of the original author.</param>
/// <param name="Content">Updated message content.</param>
/// <param name="SentAt">Timestamp when the message was originally sent on Discord.</param>
/// <param name="EditedAt">Timestamp of this edit.</param>
/// <param name="ViaDwbhub">Always <c>true</c> for user-edited messages (only dwbhub-sent messages can be edited).</param>
/// <param name="DiscordMessageId">Discord snowflake of the message.</param>
public sealed record EditMessageResponse(
    long Id,
    Guid PublicId,
    string AuthorName,
    string Content,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    bool ViaDwbhub,
    long DiscordMessageId);

/// <summary>
/// Response body for a 422 Unprocessable Entity when the 10-minute edit window has expired.
/// </summary>
/// <param name="Error">Always <c>"edit_window_expired"</c>.</param>
/// <param name="RetryAfterSeconds">Always <c>0</c> — the window cannot be re-opened.</param>
public sealed record EditWindowExpiredResponse(
    string Error = "edit_window_expired",
    int RetryAfterSeconds = 0);
