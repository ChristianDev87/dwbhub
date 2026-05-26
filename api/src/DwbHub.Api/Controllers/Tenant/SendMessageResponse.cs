namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Response body for a successfully sent message.
/// </summary>
public sealed record SendMessageResponse(long Id, long DiscordMessageId, DateTimeOffset SentAt);
