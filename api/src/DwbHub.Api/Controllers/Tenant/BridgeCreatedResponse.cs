namespace DwbHub.Api.Controllers.Tenant;

/// <summary>Response body for POST /api/t/{slug}/channels/{channelPublicId}/bridge (202 Accepted).</summary>
/// <param name="BackfillJobId">Internal ID of the backfill job that was enqueued for this channel.</param>
public sealed record BridgeCreatedResponse(long BackfillJobId);
