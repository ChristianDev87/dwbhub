namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Returned with HTTP 412 from DELETE /api/t/.../messages/{messageId} when the bot lacks
/// the MANAGE_MESSAGES Discord permission required to delete an inbound (Discord-user) message.
/// </summary>
/// <param name="Error">Stable machine-readable error code: <c>"bot_missing_manage_messages"</c>.</param>
public sealed record BotMissingPermissionResponse(string Error);
