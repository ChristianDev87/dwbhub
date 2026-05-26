namespace DwbHub.Api.Controllers.Setup;

/// <summary>Response body for GET /api/setup/status.</summary>
/// <param name="Completed">Whether the first-run setup has been completed.</param>
/// <param name="CompletedAt">Timestamp of when setup was completed, or <c>null</c> if not yet done.</param>
public sealed record SetupStatusResponse(bool Completed, DateTimeOffset? CompletedAt);
