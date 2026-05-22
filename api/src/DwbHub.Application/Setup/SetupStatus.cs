namespace DwbHub.Application.Setup;

public sealed record SetupStatus(bool Completed, DateTimeOffset? CompletedAt);
