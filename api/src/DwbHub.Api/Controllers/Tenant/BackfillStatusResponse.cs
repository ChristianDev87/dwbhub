namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Response for GET /channels/{channelPublicId}/backfill-status.
/// Returns 404 when no backfill job exists for the channel.
/// </summary>
public sealed record BackfillStatusResponse(BackfillStatusItem Status);
