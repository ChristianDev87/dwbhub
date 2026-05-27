namespace DwbHub.Api.Controllers.Tenant;

/// <summary>Response body for a successfully sent message (201 Created).</summary>
/// <param name="Id">Internal database ID of the persisted message row.</param>
/// <param name="PublicId">Public UUID used in PATCH/DELETE URLs for this message.</param>
/// <param name="DiscordMessageId">Discord snowflake of the message created by the webhook.</param>
/// <param name="SentAt">Timestamp when the message was sent.</param>
public sealed record SendMessageResponse(long Id, Guid PublicId, long DiscordMessageId, DateTimeOffset SentAt);
