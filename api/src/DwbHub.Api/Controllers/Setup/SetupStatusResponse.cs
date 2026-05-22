namespace DwbHub.Api.Controllers.Setup;

public sealed record SetupStatusResponse(bool Completed, DateTimeOffset? CompletedAt);
